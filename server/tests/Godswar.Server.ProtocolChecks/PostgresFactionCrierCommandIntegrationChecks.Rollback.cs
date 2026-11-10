using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Infrastructure.FactionCrier;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    private static async Task AssertTerminalRollbackAsync(
        NpgsqlDataSource dataSource,
        PostgresFactionCrierCommandExecutor executor)
    {
        var missing = await CreateFixtureAsync(
            dataSource,
            "missing",
            silver: 200_000,
            gold: 0,
            bindingGold: 0,
            [
                new NameplateStack(3820, 0),
                new NameplateStack(3822, 1)
            ]);
        var missingResult = await executor.ExecuteAsync(CreateEnvelope(
            missing,
            FactionCrierOperationIdentity.SecureClient(Guid.NewGuid()),
            subId: 110,
            realmPeriodDayNumber: 0));
        Check.Equal(
            (int)FactionCrierExecutionDisposition.MissingNameplate,
            (int)missingResult.Disposition,
            "missing Nameplate rejects turn-in");
        await AssertNoMutationAsync(dataSource, missing, "missing Nameplate");
        Check.True(
            await ReadNameplateAsync(dataSource, missing, 3820) ==
                new NameplateState(1, 1) &&
            await ReadNameplateAsync(dataSource, missing, 3822) ==
                new NameplateState(1, 1),
            "missing-Nameplate rollback preserves available inputs");

        var insufficient = await CreateFixtureAsync(
            dataSource,
            "nocash",
            silver: 0,
            gold: 0,
            bindingGold: 100,
            [
                new NameplateStack(3820, 0),
                new NameplateStack(3822, 1),
                new NameplateStack(3824, 2)
            ]);
        var insufficientResult = await executor.ExecuteAsync(CreateEnvelope(
            insufficient,
            FactionCrierOperationIdentity.SecureClient(Guid.NewGuid()),
            subId: 112,
            realmPeriodDayNumber: 0));
        Check.Equal(
            (int)FactionCrierExecutionDisposition.InsufficientCurrency,
            (int)insufficientResult.Disposition,
            "insufficient B-Gold rejects turn-in");
        await AssertNoMutationAsync(
            dataSource,
            insufficient,
            "insufficient B-Gold");
        foreach (var itemId in new[] { 3820, 3822, 3824 })
        {
            Check.True(
                await ReadNameplateAsync(dataSource, insufficient, itemId) ==
                    new NameplateState(1, 1),
                $"insufficient-currency rollback preserves {itemId}");
        }
    }

    private static async Task AssertNoMutationAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture,
        string description)
    {
        var state = await ReadStateAsync(dataSource, fixture);
        Check.True(
            state.WalletRevision == 0 &&
            state.InventoryRevision == 0 &&
            state.ProgressionRevision == 0 &&
            state.FactionCrierRevision == 0 &&
            state.Silver == fixture.InitialSilver &&
            state.Gold == fixture.InitialGold &&
            state.BindingGold == fixture.InitialBindingGold &&
            state.InboxCount == 0 &&
            state.AuditCount == 0 &&
            state.OutboxCount == 0 &&
            state.InventoryLedgerCount == 0 &&
            state.CurrencyLedgerCount == 0 &&
            state.DailyCount == 0 &&
            state.WeeklyCount == 0 &&
            state.ExchangeCount == 0 &&
            state.DuplicateCount == 0 &&
            state.RequestConflictCount == 0 &&
            state.WalletReconciled &&
            state.InventoryReconciled,
            $"{description} rolls back wallet, items, and all durable evidence");
    }
}
