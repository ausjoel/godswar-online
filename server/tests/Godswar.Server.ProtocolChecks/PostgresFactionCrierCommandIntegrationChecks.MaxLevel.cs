using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Infrastructure.FactionCrier;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    private static async Task AssertMaxLevelExperienceTurnInAsync(
        NpgsqlDataSource dataSource,
        PostgresFactionCrierCommandExecutor executor,
        FactionCrierBalanceSnapshot balance)
    {
        var tier = balance.Tiers.Single(value =>
            value.MinimumLevel <= 200 && value.MaximumLevel >= 200);
        var fixture = await CreateFixtureAsync(
            dataSource,
            "maxexp",
            silver: tier.TripleSilverCost + 1_000,
            gold: 0,
            bindingGold: 0,
            items:
            [
                new NameplateStack(3820, 0),
                new NameplateStack(3822, 1),
                new NameplateStack(3824, 2)
            ],
            level: 200);
        var identity = FactionCrierOperationIdentity.SecureClient(
            Guid.NewGuid());
        var envelope = CreateEnvelope(
            fixture,
            identity,
            subId: 110,
            realmPeriodDayNumber: 0);
        var committed = RequireReceipt(
            await executor.ExecuteAsync(envelope),
            FactionCrierExecutionDisposition.Committed,
            "max-level EXP turn-in");
        var duplicate = RequireReceipt(
            await executor.ExecuteAsync(envelope),
            FactionCrierExecutionDisposition.Duplicate,
            "max-level EXP turn-in replay");
        AssertSameReceipt(
            committed,
            duplicate,
            "max-level EXP turn-in replay");

        var state = await ReadStateAsync(dataSource, fixture);
        AssertCommitAuthority(
            state,
            committed,
            expectedInventoryLedgerCount: 3,
            expectedCurrencyCode: "silver",
            expectedCurrencyDelta: -tier.TripleSilverCost,
            dailyCount: 0,
            weeklyCount: 0,
            exchangeCount: 1,
            "max-level EXP turn-in");
        Check.True(
            committed.PreviousLevel == 200 &&
            committed.CurrentLevel == 200 &&
            committed.PreviousExperience == 0 &&
            committed.CurrentExperience == 0 &&
            committed.AwardedExperience == 0 &&
            committed.LevelUps.Count == 0 &&
            committed.AwardedTalentPoints == 0 &&
            committed.WalletRevision == 1 &&
            committed.InventoryRevision == 1 &&
            committed.ProgressionRevision == 1 &&
            committed.FactionCrierRevision == 1 &&
            state.Silver == 1_000 &&
            state.Experience == 0 &&
            state.DuplicateCount == 1,
            "max-level success debits once without EXP or level-up projection");
        foreach (var itemId in new[] { 3820, 3822, 3824 })
        {
            Check.True(
                await ReadNameplateAsync(dataSource, fixture, itemId) ==
                    new NameplateState(0, 0),
                $"max-level turn-in consumes {itemId} exactly once");
        }
        await AssertExchangeSettlementAsync(
            dataSource,
            fixture,
            committed,
            operation: "turn_in",
            subId: 110,
            consumedItems: [3820, 3822, 3824],
            grantedItemId: null,
            currencyCode: "silver",
            currencyCost: tier.TripleSilverCost);
    }
}
