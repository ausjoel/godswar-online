using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string BloodfangAppearanceCheckName =
        "PostgreSQL Bloodfang jade preserves historical dragon stats and skills";

    public static async Task RunBloodfangAppearanceAsync()
    {
        var connectionString = ReadRequiredConnectionString();
        await using var source = NpgsqlDataSource.Create(connectionString);
        await AssertDisposableDatabaseAsync(source);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();
        var current = PetContentBaseline.Create();
        await PostgresPetContentPublicationIntegrationChecks.AssertBloodfangV10UpgradeAsync(
            source, store.ItemContent.Templates, current.Revision.Sha256);
        var old = BloodfangPetContentChecks.CreateV10(current);
        var learned = await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(connectionString);
        var merge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(source);
        var fixture = await CreateFixtureAsync(connectionString, PetAptitude.Smart);
        await using (var egg = source.CreateCommand(
            "UPDATE character_items SET prop_id=10161 WHERE user_id=@user AND prop_id=10150;"))
        {
            egg.Parameters.AddWithValue("user", fixture.CharacterId);
            Check.Equal(1, await egg.ExecuteNonQueryAsync(), "appearance fixture selects a Blue Crystal Dragon egg");
        }
        var options = new PostgresOutboxDispatcherOptions();
        var historicalExecutor = new PostgresPetDurableCommandExecutor(source, options,
            store.ItemContent, old, merge, learned, new FixedPetHatchRankRollSource(89));
        var executor = new PostgresPetDurableCommandExecutor(source, options,
            store.ItemContent, store.PetContent, merge, learned, new ThrowingPetHatchRankRollSource());
        var correlation = new CommandConnectionCorrelation(Guid.NewGuid(), CommandTransportKind.SecureTlsLegacy);
        var subject = new CommandSubject(fixture.AccountId, fixture.CharacterId);
        var hatch = await historicalExecutor.ExecuteAsync(PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.Create(subject, correlation, DateTimeOffset.UtcNow,
                new BagItemActivationCommand(Guid.NewGuid(), fixture.EggSlot))));
        Check.True(hatch.Receipt?.Status == PetDurableReceiptStatus.EggHatched &&
            hatch.Receipt.HatchRank?.ContentRevision == BloodfangPetContentChecks.V10Revision,
            "Blue Crystal Dragon is genuinely born against the old V10 publication");
        var petId = hatch.Receipt!.PetId;
        await ExecuteVampiricFixtureSqlAsync(source, petId,
            """
            UPDATE character_pets SET opened_skill_slots=3, available_skill_slots=3 WHERE id=@petId;
            INSERT INTO character_pet_skills
                (pet_id,skill_id,slot_index,skill_rank,skill_experience,is_active,revision)
            VALUES (@petId,800,1,1,0,true,0), (@petId,6405,2,6,0,true,0);
            """);
        var call = await executor.ExecuteAsync(PlayerOwnershipTestFences.Bind(
            PetPresenceTransitionCommandEnvelope.Create(subject, correlation, DateTimeOffset.UtcNow,
                new PetPresenceTransitionCommand(Guid.NewGuid(), petId, PetPresenceCommandOperation.CallOut))));
        Check.True(call.Receipt?.IsSummoned == true, "historical dragon is summoned for its jade change");
        var jade = await SeedBagItemAsync(source, fixture.CharacterId, MagicJadeSlot, 11096, 2);
        var before = await ReadPetAppearanceStateAsync(source, fixture.CharacterId, petId);
        var beforeStats = await ReadExtractionOwnerStatsAsync(source, store.ItemContent, learned,
            fixture.AccountId, fixture.CharacterId);
        var envelope = PlayerOwnershipTestFences.Bind(PetAppearanceChangeCommandEnvelope.Create(
            subject, correlation, DateTimeOffset.UtcNow, new PetAppearanceChangeCommand(
                PetCommandOperationIdentity.SecureClient(Guid.NewGuid()), MagicJadeSlot)));
        var result = await executor.ExecuteAsync(envelope);
        var after = await ReadPetAppearanceStateAsync(source, fixture.CharacterId, petId);
        Check.True(result.Receipt?.Status == PetDurableReceiptStatus.PetAppearanceChanged &&
            result.Receipt.AppearanceChange is { OldSpeciesId: 12, NewSpeciesId: 46, MagicJadeItemId: 11096 } &&
            before.SpeciesId == 12 && after.SpeciesId == 46 && SameAppearancePayload(before, after) &&
            after.PetRevision == before.PetRevision + 1 && await ReadItemStackAsync(source, jade) == 1,
            "Bloodfang jade changes only species/revision and preserves all pet, stat, skill and bonus fields");
        await using var reconnected = new PostgresGameStore(connectionString);
        await reconnected.EnsureSeedDataAsync();
        var pet = (await reconnected.GetOwnedPetsAsync(fixture.AccountId, fixture.CharacterId)).Single();
        Check.True(pet.SpeciesId == 46 && pet.Skills.OrderBy(static skill => skill.SlotIndex)
            .Select(static skill => skill.SkillId).SequenceEqual(new[] { 2800, 800, 6405 }),
            "reconnect accepts Bloodfang with the original dragon starter and learned skills");
        Check.Equal(beforeStats, await ReadExtractionOwnerStatsAsync(source, reconnected.ItemContent,
            learned, fixture.AccountId, fixture.CharacterId), "appearance preserves effective owner healing stats");
        var replay = await executor.ExecuteAsync(envelope);
        Check.True(replay.Disposition == PetDurableExecutionDisposition.Duplicate &&
            await ReadItemStackAsync(source, jade) == 1,
            "Bloodfang jade replay consumes no additional jade");
    }
}
