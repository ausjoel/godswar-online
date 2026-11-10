using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Infrastructure.FactionCrier;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    private static async Task AssertRenewalAndStaleSelectionAsync(
        NpgsqlDataSource dataSource,
        PostgresFactionCrierCommandExecutor executor,
        FactionCrierBalanceSnapshot balance)
    {
        const short sourceSlot = 7;
        var fixture = await CreateFixtureAsync(
            dataSource,
            "renew",
            silver: 0,
            gold: 1_000,
            bindingGold: 0,
            [new NameplateStack(3820, sourceSlot, 2)]);
        var selection = new FactionCrierNameplateSelection(
            sourceSlot,
            3820,
            CompactNameplate(3820, 2));
        var receipt = RequireReceipt(
            await executor.ExecuteAsync(CreateEnvelope(
                fixture,
                FactionCrierOperationIdentity.SecureClient(Guid.NewGuid()),
                subId: 42,
                realmPeriodDayNumber: 0,
                renewalSource: selection)),
            FactionCrierExecutionDisposition.Committed,
            "Nameplate renewal");
        var state = await ReadStateAsync(dataSource, fixture);
        AssertCommitAuthority(
            state,
            receipt,
            expectedInventoryLedgerCount: 2,
            expectedCurrencyCode: "gold",
            expectedCurrencyDelta: -balance.RenewalGoldCost,
            dailyCount: 0,
            weeklyCount: 0,
            exchangeCount: 1,
            "Nameplate renewal");
        Check.True(
            state.Gold == 1_000 - balance.RenewalGoldCost &&
            receipt.WalletRevision == 1 &&
            receipt.InventoryRevision == 1 &&
            receipt.ProgressionRevision == 0 &&
            receipt.FactionCrierRevision == 1 &&
            await ReadNameplateAsync(dataSource, fixture, 3820) ==
                new NameplateState(1, 1) &&
            await ReadNameplateAsync(dataSource, fixture, 3821) ==
                new NameplateState(1, 1),
            "renewal converts one selected stack unit into one target");
        await AssertExchangeSettlementAsync(
            dataSource,
            fixture,
            receipt,
            operation: "renewal",
            subId: 42,
            consumedItems: [3820],
            grantedItemId: 3821,
            currencyCode: "gold",
            currencyCost: balance.RenewalGoldCost);
        await AssertRenewalEvidenceGuardAsync(dataSource, fixture);

        var stale = await CreateFixtureAsync(
            dataSource,
            "stale",
            silver: 0,
            gold: 1_000,
            bindingGold: 0,
            [new NameplateStack(3820, sourceSlot, 2)]);
        var staleResult = await executor.ExecuteAsync(CreateEnvelope(
            stale,
            FactionCrierOperationIdentity.SecureClient(Guid.NewGuid()),
            subId: 42,
            realmPeriodDayNumber: 0,
            renewalSource: new FactionCrierNameplateSelection(
                sourceSlot,
                3820,
                CompactNameplate(3820, 1))));
        Check.Equal(
            (int)FactionCrierExecutionDisposition.MissingNameplate,
            (int)staleResult.Disposition,
            "stale selected-stack state is rejected");
        await AssertNoMutationAsync(dataSource, stale, "stale renewal");
        Check.True(
            await ReadNameplateAsync(dataSource, stale, 3820) ==
                new NameplateState(1, 2),
            "stale renewal preserves the selected stack");
    }

    private static async Task AssertPaidTurnInsAsync(
        NpgsqlDataSource dataSource,
        PostgresFactionCrierCommandExecutor executor,
        FactionCrierBalanceSnapshot balance)
    {
        var tier = balance.Tiers.Single(value =>
            value.MinimumLevel <= 80 && value.MaximumLevel >= 80);
        var cases = new[]
        {
            new PaidTurnInCase(
                "silver", 110, "silver", tier.TripleSilverCost,
                tier.TripleSilverCost + 1_000, 0, 0),
            CreatePremiumCase(balance, "bgold", 112, "binding_gold"),
            CreatePremiumCase(balance, "gold", 114, "gold")
        };
        foreach (var testCase in cases)
        {
            var fixture = await CreateFixtureAsync(
                dataSource,
                testCase.Scenario,
                testCase.InitialSilver,
                testCase.InitialGold,
                testCase.InitialBindingGold,
                [
                    new NameplateStack(3820, 0),
                    new NameplateStack(3822, 1),
                    new NameplateStack(3824, 2)
                ]);
            var receipt = RequireReceipt(
                await executor.ExecuteAsync(CreateEnvelope(
                    fixture,
                    FactionCrierOperationIdentity.SecureClient(Guid.NewGuid()),
                    testCase.SubId,
                    realmPeriodDayNumber: 0)),
                FactionCrierExecutionDisposition.Committed,
                $"{testCase.CurrencyCode} turn-in");
            var state = await ReadStateAsync(dataSource, fixture);
            AssertCommitAuthority(
                state,
                receipt,
                expectedInventoryLedgerCount: 3,
                expectedCurrencyCode: testCase.CurrencyCode,
                expectedCurrencyDelta: -testCase.Cost,
                dailyCount: 0,
                weeklyCount: 0,
                exchangeCount: 1,
                $"{testCase.CurrencyCode} turn-in");
            Check.True(
                receipt.WalletRevision == 1 &&
                receipt.InventoryRevision == 1 &&
                receipt.ProgressionRevision == 1 &&
                receipt.FactionCrierRevision == 1 &&
                receipt.AwardedExperience > 0 &&
                receipt.AwardedTalentPoints == 0 &&
                state.Silver == testCase.InitialSilver -
                    (testCase.CurrencyCode == "silver" ? testCase.Cost : 0) &&
                state.Gold == testCase.InitialGold -
                    (testCase.CurrencyCode == "gold" ? testCase.Cost : 0) &&
                state.BindingGold == testCase.InitialBindingGold -
                    (testCase.CurrencyCode == "binding_gold"
                        ? testCase.Cost
                        : 0),
                $"{testCase.CurrencyCode} turn-in debits only its wallet");
            foreach (var itemId in new[] { 3820, 3822, 3824 })
            {
                Check.True(
                    await ReadNameplateAsync(dataSource, fixture, itemId) ==
                        new NameplateState(0, 0),
                    $"{testCase.CurrencyCode} turn-in consumes {itemId}");
            }
            await AssertExchangeSettlementAsync(
                dataSource,
                fixture,
                receipt,
                operation: "turn_in",
                subId: testCase.SubId,
                consumedItems: [3820, 3822, 3824],
                grantedItemId: null,
                currencyCode: testCase.CurrencyCode,
                currencyCost: testCase.Cost);
            if (testCase.SubId == 110)
            {
                await AssertTurnInEvidenceGuardAsync(dataSource, fixture);
            }
        }
    }

    private static PaidTurnInCase CreatePremiumCase(
        FactionCrierBalanceSnapshot balance,
        string scenario,
        int subId,
        string currencyCode)
    {
        var cost = balance.Options.Single(value => value.SubId == subId).Cost;
        return currencyCode == "binding_gold"
            ? new(scenario, subId, currencyCode, cost, 0, 0, cost + 1_000)
            : new(scenario, subId, currencyCode, cost, 0, cost + 1_000, 0);
    }

    private sealed record PaidTurnInCase(
        string Scenario,
        int SubId,
        string CurrencyCode,
        int Cost,
        int InitialSilver,
        int InitialGold,
        int InitialBindingGold);
}
