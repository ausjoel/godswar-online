using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string AtlantisCaptureCheckName = "PostgreSQL Atlantis Merman capture atomicity and replay";

    public static async Task RunAtlantisCaptureAsync()
    {
        var connectionString = ReadRequiredConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await AssertDisposableDatabaseAsync(dataSource);
        await new PostgresSchemaMigrationRunner(dataSource).InitializeGodswarSchemaAsync();
        GameplayItemContent items;
        IPetContentCatalog pets;
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            items = store.ItemContent;
            pets = store.PetContent;
        }
        Check.True(pets.TryGetSpeciesByEggItemId(10158, out var merman) && merman.SpeciesId == 9 &&
            pets.TryGetNativeProfile(9, 1, out _), "published Merman egg and quality-one native profile exist");
        var ownerMerge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(dataSource);
        // FINALISE-PETS (2026-11-05): the Merman aptitude is rolled; a fixed roll keeps the check exact, and its call
        // count proves a replay never rolls again.
        var rolls = new FixedPetCaptureRarityRollSource(0);
        PostgresPetDurableCommandExecutor Executor() => new(dataSource, new PostgresOutboxDispatcherOptions(),
            items, pets, ownerMerge, PetLearnedSkillContentBaseline.Create(),
            petCaptureRarityRollSource: rolls);

        foreach (var stack in new[] { 1, 2 })
        {
            var fixture = await CreateFixtureAsync(connectionString);
            await ReplaceFixtureEggWithNetAsync(dataSource, fixture);
            await using (var update = dataSource.CreateCommand(
                "UPDATE public.character_items SET stack = @stack WHERE user_id = @character AND slot_index = 90 AND item_location = 1;"))
            {
                update.Parameters.AddWithValue("stack", stack);
                update.Parameters.AddWithValue("character", fixture.CharacterId);
                Check.Equal(1, await update.ExecuteNonQueryAsync(), "fixture has one capture-net stack");
            }
            var envelope = AtlantisCaptureEnvelope(fixture);
            var before = await ReadInventoryStateAsync(dataSource, fixture.CharacterId);
            await InstallAtlantisEggFailureAsync(dataSource, fixture.CharacterId);
            try
            {
                try
                {
                    await Executor().ExecuteAsync(envelope);
                    throw new InvalidOperationException("The injected egg insertion failure did not occur.");
                }
                catch (PostgresException error) when (error.SqlState == "P0001" && error.MessageText == "atlantis capture rollback proof")
                {
                }
                var failed = await ReadAtlantisCaptureBagAsync(dataSource, fixture.CharacterId);
                var rolledBack = await ReadInventoryStateAsync(dataSource, fixture.CharacterId);
                Check.True(failed == (stack, 0, 0) && rolledBack.InventoryRevision == before.InventoryRevision &&
                    rolledBack.LedgerEntryCount == before.LedgerEntryCount && rolledBack.OutboxCount == before.OutboxCount,
                    "egg failure rolls back net decrement or transformation and all inventory evidence");
            }
            finally
            {
                await RemoveAtlantisEggFailureAsync(dataSource);
            }

            var concurrent = await Task.WhenAll(Executor().ExecuteAsync(envelope), Executor().ExecuteAsync(envelope));
            AssertCommitAndDuplicate(concurrent, PetDurableReceiptStatus.PetCaptured, "concurrent Atlantis capture");
            var committed = concurrent.Single(value => value.Disposition == PetDurableExecutionDisposition.Committed).Receipt!;
            var state = await ReadAtlantisCaptureBagAsync(dataSource, fixture.CharacterId);
            var inventory = await ReadInventoryStateAsync(dataSource, fixture.CharacterId);
            var rollsAfterCommit = rolls.CallCount;
            Check.True(state.Nets == stack - 1 && state.Eggs == 1 && state.Quality >= 1 && inventory.InventoryRevision == before.InventoryRevision + 1 &&
                inventory.LedgerEntryCount == before.LedgerEntryCount + stack && inventory.IsReconciled,
                "one net becomes one Merman egg (rolled aptitude) in a single reconciled revision");
            var replay = await Executor().ExecuteAsync(envelope);
            Check.True(replay.Disposition == PetDurableExecutionDisposition.Duplicate && replay.Receipt == committed && rolls.CallCount == rollsAfterCommit &&
                await ReadAtlantisCaptureBagAsync(dataSource, fixture.CharacterId) == state,
                "executor restart replays the exact egg receipt without consuming another net or rolling Medusa rarity");

            // Same operation, different target evidence must not claim another egg.
            var conflict = envelope with { Command = envelope.Command with
            {
                Capture = envelope.Command.Capture!.Value with { TargetObjectId = AtlantisPetSpawnPolicy.SirenObjectId }
            } };
            var rejected = await Executor().ExecuteAsync(conflict);
            Check.True(rejected.Disposition == PetDurableExecutionDisposition.RequestHashConflict &&
                await ReadAtlantisCaptureBagAsync(dataSource, fixture.CharacterId) == state,
                "different capture evidence cannot reuse a committed request hash");
        }
        await AssertStoredCaptureWeightsAsync(dataSource);

        // F-122: a Mermaid net becomes one quality-one Morning Dew 3, no egg, no rarity roll.
        var dewFixture = await CreateFixtureAsync(connectionString);
        await ReplaceFixtureEggWithNetAsync(dataSource, dewFixture);
        var dewEnvelope = AtlantisCaptureEnvelope(dewFixture, 10132, PetCaptureContext.AtlantisMermaidDew);
        var rollsBeforeDew = rolls.CallCount;
        var dewResult = await Executor().ExecuteAsync(dewEnvelope);
        var dewReplay = await Executor().ExecuteAsync(dewEnvelope);
        Check.True(rolls.CallCount == rollsBeforeDew && dewReplay.Disposition == PetDurableExecutionDisposition.Duplicate &&
            dewReplay.Receipt == dewResult.Receipt, $"F-122: the Mermaid capture rolls nothing and replays its receipt (rolls {rollsBeforeDew}->{rolls.CallCount}, first {dewResult.Disposition}/{dewResult.Receipt?.Status}, replay {dewReplay.Disposition}/{dewReplay.Receipt?.Status})");
        await using (var read = dataSource.CreateCommand("""
            SELECT count(*) FILTER (WHERE prop_id = 10084)::integer, count(*) FILTER (WHERE prop_id = 10158)::integer,
                   count(*) FILTER (WHERE prop_id = 10132 AND stack = 1 AND item_quality = 1)::integer
            FROM public.character_items WHERE user_id = @character AND item_location = 1;
            """))
        {
            read.Parameters.AddWithValue("character", dewFixture.CharacterId);
            await using var reader = await read.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync() && dewResult.Disposition == PetDurableExecutionDisposition.Committed &&
                dewResult.Receipt?.Status == PetDurableReceiptStatus.PetCaptured &&
                reader.GetInt32(0) == 0 && reader.GetInt32(1) == 0 && reader.GetInt32(2) == 1,
                "F-122: the Mermaid net becomes one Morning Dew, not a Merman egg");
        }
    }

    private static CommandEnvelope<BagItemActivationCommand> AtlantisCaptureEnvelope(PetFixture fixture,
        uint itemId = 10158, PetCaptureContext context = PetCaptureContext.AtlantisMerman)
    {
        var correlation = new CommandConnectionCorrelation(Guid.NewGuid(), CommandTransportKind.LegacyTcp);
        return PlayerOwnershipTestFences.Bind(BagItemActivationCommandEnvelope.CreateServerSessionLifecycle(
            new(fixture.AccountId, fixture.CharacterId), correlation, DateTimeOffset.UtcNow,
            new(PetCommandOperationIdentity.ServerSessionLifecycle(Guid.NewGuid(), correlation.ConnectionId),
                fixture.EggSlot, Capture: new(AtlantisPetSpawnPolicy.MermaidObjectId, Guid.NewGuid(), 1, 1,
                    itemId, MedusaEncounterDifficulty.Normal, context))));
    }

    private static async Task<(int Nets, int Eggs, int Quality)> ReadAtlantisCaptureBagAsync(
        NpgsqlDataSource dataSource, int characterId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT coalesce(sum(stack) FILTER (WHERE prop_id = 10084), 0)::integer,
                   count(*) FILTER (WHERE prop_id = 10158)::integer,
                   coalesce(min(item_quality) FILTER (WHERE prop_id = 10158), 0)::integer
            FROM public.character_items WHERE user_id = @character AND item_location = 1;
            """);
        command.Parameters.AddWithValue("character", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "capture bag projection exists");
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task InstallAtlantisEggFailureAsync(NpgsqlDataSource dataSource, int characterId)
    {
        // This function exists only inside the explicitly verified disposable
        // database and fails after the stacked-net decrement has executed.
        await using var command = dataSource.CreateCommand($$"""
            CREATE FUNCTION public.atlantis_capture_rollback_proof() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN
                IF NEW.user_id = {{characterId}} AND NEW.prop_id = 10158 THEN
                    RAISE EXCEPTION 'atlantis capture rollback proof';
                END IF;
                RETURN NEW;
            END;
            $proof$;
            CREATE TRIGGER atlantis_capture_rollback_proof BEFORE INSERT OR UPDATE ON public.character_items
            FOR EACH ROW EXECUTE FUNCTION public.atlantis_capture_rollback_proof();
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveAtlantisEggFailureAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            DROP TRIGGER IF EXISTS atlantis_capture_rollback_proof ON public.character_items;
            DROP FUNCTION IF EXISTS public.atlantis_capture_rollback_proof();
            """);
        await command.ExecuteNonQueryAsync();
    }
}
