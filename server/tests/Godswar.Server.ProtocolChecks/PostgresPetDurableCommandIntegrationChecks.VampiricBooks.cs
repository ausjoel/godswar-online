using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string VampiricBookCheckName =
        "PostgreSQL shared Vampiric book learning, six upgrades and native projection";

    public static async Task RunVampiricBooksAsync()
    {
        var connectionString = ReadRequiredConnectionString();
        await using var source = NpgsqlDataSource.Create(connectionString);
        await AssertDisposableDatabaseAsync(source);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();
        var items = store.ItemContent;
        var pets = store.PetContent;
        var learned = await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(connectionString);
        var merge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(source);
        var fixture = await CreateFixtureAsync(connectionString, PetAptitude.Smart);
        var species = pets.Species.Single(value => value.SpeciesId == 12);
        Check.True(species.StarterSkillId == 2800 && species.EggItemId.HasValue,
            "the real Blue Crystal Dragon has its existing exclusive Iceshot starter");
        await using (var egg = source.CreateCommand(
            """
            UPDATE public.character_items SET prop_id = @egg
            WHERE user_id = @characterId AND item_location = 1 AND slot_index = @slot AND prop_id = 10150;
            """))
        {
            egg.Parameters.AddWithValue("egg", checked((int)species.EggItemId!.Value));
            egg.Parameters.AddWithValue("characterId", fixture.CharacterId);
            egg.Parameters.AddWithValue("slot", fixture.EggSlot);
            Check.Equal(1, await egg.ExecuteNonQueryAsync(), "select one actual Blue Crystal Dragon egg");
        }
        var executor = new PostgresPetDurableCommandExecutor(source,
            new PostgresOutboxDispatcherOptions(), items, pets, merge, learned,
            new FixedPetHatchRankRollSource(89));
        var restarted = new PostgresPetDurableCommandExecutor(source,
            new PostgresOutboxDispatcherOptions(), items, pets, merge, learned,
            new ThrowingPetHatchRankRollSource());
        var subject = new CommandSubject(fixture.AccountId, fixture.CharacterId);
        var correlation = new CommandConnectionCorrelation(Guid.NewGuid(), CommandTransportKind.LegacyTcp);
        CommandEnvelope<BagItemActivationCommand> Command(int slot) => PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(subject, correlation, DateTimeOffset.UtcNow,
                new BagItemActivationCommand(PetCommandOperationIdentity.RawLocalServer(
                    Guid.NewGuid(), correlation.ConnectionId), slot)));
        var hatch = await executor.ExecuteAsync(Command(fixture.EggSlot));
        Check.True(hatch.Disposition == PetDurableExecutionDisposition.Committed &&
            hatch.Receipt?.Status == PetDurableReceiptStatus.EggHatched,
            "Vampiric books use a genuinely hatched owned pet");
        var petId = hatch.Receipt!.PetId;
        await using (var prepare = source.CreateCommand(
            """
            UPDATE public.character_pets SET opened_skill_slots = 2, available_skill_slots = 2
            WHERE id = @petId AND user_id = @characterId AND species_id = 12 AND is_carried;
            UPDATE public.character_pet_stat_values SET initial_savvy = 400
            WHERE pet_id = @petId AND stat_code = 3;
            """))
        {
            prepare.Parameters.AddWithValue("petId", petId);
            prepare.Parameters.AddWithValue("characterId", fixture.CharacterId);
            Check.Equal(2, await prepare.ExecuteNonQueryAsync(),
                "fixture opens one additional slot and satisfies all Accuracy thresholds");
        }
        var original = (await store.GetOwnedPetsAsync(fixture.AccountId, fixture.CharacterId))
            .Single(pet => pet.PetId == petId);
        var starter = original.Skills.Single();
        Check.Equal(2800, starter.SkillId, "hatching retains native Iceshot");
        var baseline = await ReadExtractionOwnerStatsAsync(source, items, learned,
            fixture.AccountId, fixture.CharacterId);
        Check.Equal(0, baseline.Percentage, "unlearned pet has no percentage contribution");

        const int highestSlot = 80;
        var highestBook = await SeedBagItemAsync(source, fixture.CharacterId, highestSlot, 16405, 2);
        var skipped = await executor.ExecuteAsync(Command(highestSlot));
        Check.True(skipped.Disposition == PetDurableExecutionDisposition.TerminalRejected &&
            skipped.Receipt?.Status == PetDurableReceiptStatus.PetSkillBookPriorTierRequired &&
            await ReadItemStackAsync(source, highestBook) == 2,
            "a shared book still cannot skip its preceding tier or consume a rejected book");

        var percentages = new[] { 600, 800, 1100, 1400, 1700, 2000 };
        for (short tier = 1; tier <= 6; tier++)
        {
            var slot = tier == 6 ? highestSlot : 80 + tier;
            var item = tier == 6 ? highestBook :
                await SeedBagItemAsync(source, fixture.CharacterId, slot, 16399 + tier, 2);
            var command = Command(slot);
            var result = await executor.ExecuteAsync(command);
            var evidence = result.Receipt?.SkillLearn;
            Check.True(result.Disposition == PetDurableExecutionDisposition.Committed &&
                result.Receipt?.Status == PetDurableReceiptStatus.PetSkillLearned &&
                evidence is not null && evidence.IsValid && evidence.SpeciesId == 12 &&
                evidence.FamilyType == 428 && evidence.PreviousPriority == tier - 1 &&
                evidence.LearnedPriority == tier && evidence.LearnedRuntimeSkillId == 6399 + tier &&
                evidence.SkillSlot == 1 && evidence.ItemInstanceId == item,
                $"Vampiric tier{tier} commits on a different starter family: {result.Disposition}/{result.Receipt?.Status}");
            var state = (await store.GetOwnedPetsAsync(fixture.AccountId, fixture.CharacterId))
                .Single(pet => pet.PetId == petId);
            Check.True(state.SpeciesId == 12 && state.Skills.Count == 2 &&
                state.Skills.Single(skill => skill.SlotIndex == 0) == starter &&
                state.Skills.Single(skill => skill.SlotIndex == 1) is { } vampiric &&
                vampiric.SkillId == 6399 + tier && vampiric.SkillRank == tier &&
                vampiric.Revision == tier - 1 && vampiric.IsActive,
                $"tier{tier} replaces one family row in its original slot and preserves the starter");
            var packet = PacketBuilder.PetSkillState(state);
            Check.True(packet.Length == 36 && packet[10] == 2 &&
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(12)) == 2800 &&
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(14)) == 6399 + tier,
                $"tier{tier} reload serializes its exact native icon/runtime identity");
            Check.Equal((baseline.Flat, percentages[tier - 1]),
                await ReadExtractionOwnerStatsAsync(source, items, learned, fixture.AccountId, fixture.CharacterId),
                $"tier{tier} becomes the actual owner percentage immediately after durable learning");
            Check.Equal((short)1, await ReadItemStackAsync(source, item), "one exact book consumed");
            var replay = await restarted.ExecuteAsync(command);
            var afterReplay = (await store.GetOwnedPetsAsync(fixture.AccountId, fixture.CharacterId))
                .Single(pet => pet.PetId == petId);
            Check.True(replay.Disposition == PetDurableExecutionDisposition.Duplicate &&
                replay.Receipt == result.Receipt && afterReplay.Revision == state.Revision &&
                afterReplay.Skills.SequenceEqual(state.Skills) && await ReadItemStackAsync(source, item) == 1,
                $"restart replay of tier{tier} changes neither skill nor inventory twice");
        }

        var wrongBook = await SeedBagItemAsync(source, fixture.CharacterId, 79, 10511, 2);
        var rejected = await executor.ExecuteAsync(Command(79));
        Check.True(rejected.Disposition == PetDurableExecutionDisposition.TerminalRejected &&
            rejected.Receipt?.Status == PetDurableReceiptStatus.PetSkillBookWrongSpecies &&
            await ReadItemStackAsync(source, wrongBook) == 2,
            "allowing Vampiric does not allow Wild Strength II on a Blue Crystal Dragon");
        var final = (await store.GetOwnedPetsAsync(fixture.AccountId, fixture.CharacterId))
            .Single(pet => pet.PetId == petId);
        Check.True(final.Skills.Count == 2 && final.Skills.Single(skill => skill.SlotIndex == 0) == starter &&
            final.Skills.Single(skill => skill.SlotIndex == 1).SkillId == 6405,
            "wrong-species rejection preserves the final VI projection and its unrelated skill");
    }
}
