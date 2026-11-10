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
    public const string WonderlandSackCheckName = "PostgreSQL Wonderland sacks consume once and freeze one weighted reward atomically";

    public static async Task RunWonderlandSacksAsync()
    {
        var connection = ReadRequiredConnectionString();
        await using var source = NpgsqlDataSource.Create(connection);
        await AssertDisposableDatabaseAsync(source);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var game = new PostgresGameStore(connection);
        await game.EnsureSeedDataAsync();
        var ownerMerge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(source);
        PostgresPetDurableCommandExecutor Executor(IWonderlandSackRollSource rolls) => new(source,
            new PostgresOutboxDispatcherOptions(), game.ItemContent, game.PetContent, ownerMerge,
            PetLearnedSkillContentBaseline.Create(), wonderlandSackRollSource: rolls);
        var fixture = await CreateFixtureAsync(connection);
        await CheckEverySackOutcomeAsync(source, fixture, Executor);
        await CheckSackConcurrencyAsync(source, fixture, Executor);
        await CheckSackCapacityAsync(source, fixture, Executor);
        await CheckSackFragmentedStacksAsync(source, fixture, Executor);
        await CheckSackRollbackAsync(source, fixture, Executor);
    }

    private static async Task CheckEverySackOutcomeAsync(NpgsqlDataSource source, PetFixture fixture,
        Func<IWonderlandSackRollSource, PostgresPetDurableCommandExecutor> executor)
    {
        foreach (var sackId in new uint[] { 4450, 4456, 4461 })
        {
            var start = 0;
            foreach (var outcome in WonderlandSackRewardPolicy.Outcomes(sackId))
            {
                await SeedWonderlandSackAsync(source, fixture, sackId, 1);
                var before = await ReadSackEvidenceAsync(source, fixture.CharacterId);
                var protectedState = await ReadSackProtectedStateAsync(source, fixture.CharacterId);
                var rolls = new SackTestRollSource(start);
                var result = await executor(rolls).ExecuteAsync(SackEnvelope(fixture));
                Check.True(result.IsSuccess && result.Receipt?.Status == PetDurableReceiptStatus.WonderlandSackOpened,
                    $"sack {sackId} commits outcome beginning at roll {start}");
                Check.Equal(outcome.ItemId, result.Receipt!.WonderlandSack!.RewardItemId, "sack receipt identifies selected item");
                Check.Equal(outcome.Quantity, result.Receipt.WonderlandSack.RewardQuantity, "sack receipt identifies exact quantity");
                Check.Equal(1, rolls.Calls, "exactly one RNG call selects a guaranteed reward");
                await AssertSackInventoryAsync(source, fixture.CharacterId, 0, outcome.ItemId, outcome.Quantity, 1);
                await AssertSackEvidenceDeltaAsync(source, fixture.CharacterId, before, ledger: 2);
                Check.Equal(protectedState, await ReadSackProtectedStateAsync(source, fixture.CharacterId),
                    "opening a sack preserves currencies, EXP, pets and equipped item attributes");
                start += outcome.Weight;
            }
        }
    }

    private static async Task CheckSackConcurrencyAsync(NpgsqlDataSource source, PetFixture fixture,
        Func<IWonderlandSackRollSource, PostgresPetDurableCommandExecutor> executor)
    {
        await SeedWonderlandSackAsync(source, fixture, 4450, 2);
        await using (var stack = source.CreateCommand("""
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack,attribute1,attribute_level1)
            VALUES(@character,1,0,10133,1,1,1,80,1,2);
            """))
        {
            stack.Parameters.AddWithValue("character", fixture.CharacterId);
            await stack.ExecuteNonQueryAsync();
        }
        var envelope = SackEnvelope(fixture);
        var rolls = new SackTestRollSource(0);
        var service = executor(rolls);
        var before = await ReadSackEvidenceAsync(source, fixture.CharacterId);
        var results = await Task.WhenAll(service.ExecuteAsync(envelope), service.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(results, PetDurableReceiptStatus.WonderlandSackOpened, "concurrent sack opening");
        Check.Equal(1, rolls.Calls, "concurrent retries draw one reward");
        await AssertSackInventoryAsync(source, fixture.CharacterId, 1, 10133, 105, 2);
        await AssertSackEvidenceDeltaAsync(source, fixture.CharacterId, before, ledger: 3);
        var after = await ReadSackBusinessStateAsync(source, fixture.CharacterId);
        var restartedRolls = new SackTestRollSource(81);
        var restarted = await executor(restartedRolls).ExecuteAsync(envelope);
        Check.True(restarted.Disposition == PetDurableExecutionDisposition.Duplicate &&
            restarted.Receipt == results[0].Receipt, "restart replays the exact frozen weighted reward receipt");
        Check.Equal(0, restartedRolls.Calls, "replay never consults RNG");
        Check.Equal(after, await ReadSackBusinessStateAsync(source, fixture.CharacterId), "replay cannot consume or grant again");
        var cooldown = await ReadConsumableCooldownAsync(source, fixture.CharacterId, 5400);
        Check.True(cooldown is { } natural && natural.ReadyAt - natural.UpdatedAt == TimeSpan.FromSeconds(1),
            "sack use starts its native one-second cooldown");
        await ExtendConsumableCooldownForAssertionAsync(source, fixture.CharacterId, 5400);
        var coolingEnvelope = SackEnvelope(fixture);
        var coolingBefore = await ReadConsumableCooldownAsync(source, fixture.CharacterId, 5400);
        var cooling = await service.ExecuteAsync(coolingEnvelope);
        var coolingReplay = await executor(restartedRolls).ExecuteAsync(coolingEnvelope);
        Check.True(cooling.Receipt?.Status == PetDurableReceiptStatus.ConsumableCooldownActive &&
            coolingReplay.Disposition == PetDurableExecutionDisposition.Duplicate && coolingReplay.Receipt == cooling.Receipt,
            "sack cooldown rejection is durable and replay-safe");
        Check.Equal(1, rolls.Calls, "cooldown rejection cannot draw a reward");
        Check.Equal(after, await ReadSackBusinessStateAsync(source, fixture.CharacterId), "cooldown preserves sack and reward stacks");
        Check.True(coolingBefore == await ReadConsumableCooldownAsync(source, fixture.CharacterId, 5400),
            "cooldown rejection does not extend the native deadline");
        await using var attributes = source.CreateCommand("""
            SELECT attribute1=1 AND attribute_level1=2 AND stack=99 FROM character_items
            WHERE user_id=@character AND item_location=1 AND slot_index=0;
            """);
        attributes.Parameters.AddWithValue("character", fixture.CharacterId);
        Check.True(await attributes.ExecuteScalarAsync() is true, "stack top-up preserves authoritative owned attributes");
    }

    private sealed class SackTestRollSource(int roll) : IWonderlandSackRollSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public int NextRoll(int exclusiveMaximum)
        {
            Interlocked.Increment(ref _calls);
            Check.True(roll >= 0 && roll < exclusiveMaximum, "test roll lies inside requested weighted range");
            return roll;
        }
    }

    private static CommandEnvelope<BagItemActivationCommand> SackEnvelope(PetFixture fixture)
    {
        var correlation = new CommandConnectionCorrelation(Guid.NewGuid(), CommandTransportKind.LegacyTcp);
        return PlayerOwnershipTestFences.Bind(BagItemActivationCommandEnvelope.CreateRawLocal(
            new(fixture.AccountId, fixture.CharacterId), correlation, DateTimeOffset.UtcNow,
            new(PetCommandOperationIdentity.RawLocalServer(Guid.NewGuid(), correlation.ConnectionId), fixture.EggSlot)));
    }
}
