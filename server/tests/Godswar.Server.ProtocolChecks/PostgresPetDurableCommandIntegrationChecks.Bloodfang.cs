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
    public const string BloodfangHatchCheckName =
        "PostgreSQL Bloodfang hatch, innate Vampiric and reconnect";

    public static async Task RunBloodfangHatchAsync()
    {
        var connectionString = ReadRequiredConnectionString();
        await using var source = NpgsqlDataSource.Create(connectionString);
        await AssertDisposableDatabaseAsync(source);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();
        var learned = await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(connectionString);
        var merge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(source);
        var fixture = await CreateFixtureAsync(connectionString, PetAptitude.Smart);
        await using (var egg = source.CreateCommand(
            """
            UPDATE public.character_items SET prop_id = 10194
            WHERE user_id = @characterId AND item_location = 1
              AND slot_index = @slot AND prop_id = 10150;
            """))
        {
            egg.Parameters.AddWithValue("characterId", fixture.CharacterId);
            egg.Parameters.AddWithValue("slot", fixture.EggSlot);
            Check.Equal(1, await egg.ExecuteNonQueryAsync(), "fixture selects a Bloodfang egg");
        }

        var executor = new PostgresPetDurableCommandExecutor(source,
            new PostgresOutboxDispatcherOptions(), store.ItemContent, store.PetContent,
            merge, learned, new FixedPetHatchRankRollSource(89));
        var correlation = new CommandConnectionCorrelation(Guid.NewGuid(), CommandTransportKind.LegacyTcp);
        var envelope = PlayerOwnershipTestFences.Bind(BagItemActivationCommandEnvelope.CreateRawLocal(
            new CommandSubject(fixture.AccountId, fixture.CharacterId), correlation, DateTimeOffset.UtcNow,
            new BagItemActivationCommand(PetCommandOperationIdentity.RawLocalServer(
                Guid.NewGuid(), correlation.ConnectionId), fixture.EggSlot)));
        var results = await Task.WhenAll(executor.ExecuteAsync(envelope), executor.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(results, PetDurableReceiptStatus.EggHatched, "Bloodfang concurrent hatch");
        var receipt = results.Single(static result =>
            result.Disposition == PetDurableExecutionDisposition.Committed).Receipt!;

        await using var reconnected = new PostgresGameStore(connectionString);
        await reconnected.EnsureSeedDataAsync();
        var pets = await reconnected.GetOwnedPetsAsync(fixture.AccountId, fixture.CharacterId);
        var bloodfang = pets.Single();
        Check.True(bloodfang.PetId == receipt.PetId && bloodfang.SpeciesId == 46 &&
            bloodfang.IsCarried && bloodfang.Level == 1 && bloodfang.Skills.Count == 1 &&
            bloodfang.Skills.Single() is { SkillId: 6400, SlotIndex: 0, SkillRank: 1, IsActive: true },
            "reconnected Bloodfang retains its original species and innate Vampiric I without a book");
        Check.Equal((0, 600), await ReadExtractionOwnerStatsAsync(source,
            reconnected.ItemContent, learned, fixture.AccountId, fixture.CharacterId),
            "hatched Bloodfang supplies six percent Vampiric through the actual owner projection");
        var restarted = new PostgresPetDurableCommandExecutor(source,
            new PostgresOutboxDispatcherOptions(), reconnected.ItemContent, reconnected.PetContent,
            merge, learned, new ThrowingPetHatchRankRollSource());
        var replay = await restarted.ExecuteAsync(envelope);
        Check.True(replay.Disposition == PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == receipt &&
            (await reconnected.GetOwnedPetsAsync(fixture.AccountId, fixture.CharacterId)).Count == 1,
            "replayed Bloodfang hatch returns its exact receipt without rerolling or creating another pet");
        await using var consumed = source.CreateCommand(
            "SELECT count(*) FROM character_items WHERE user_id=@characterId AND prop_id=10194;");
        consumed.Parameters.AddWithValue("characterId", fixture.CharacterId);
        Check.Equal(0L, (long)(await consumed.ExecuteScalarAsync())!, "Bloodfang hatch consumes its exact egg");
    }
}
