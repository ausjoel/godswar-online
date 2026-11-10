using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Realms;
using Godswar.Server.Infrastructure.FactionCrier;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    private static async Task AssertDailyCommitReplayAndClaimFenceAsync(
        NpgsqlDataSource dataSource,
        PostgresFactionCrierCommandExecutor executor,
        FactionCrierBalanceSnapshot balance)
    {
        var fixture = await CreateFixtureAsync(
            dataSource,
            "daily",
            silver: 0,
            gold: 0,
            bindingGold: 0);
        var day = TestRealmCalendar.GetDay(ReviewedMondayInstant);
        Check.Equal(
            (int)DayOfWeek.Monday,
            (int)day.DayOfWeek,
            "daily fixture uses reviewed realm Monday");
        var identity = FactionCrierOperationIdentity.SecureClient(
            Guid.NewGuid());
        var envelope = CreateEnvelope(
            fixture,
            identity,
            subId: 1,
            realmPeriodDayNumber: day.DayNumber);
        var committed = RequireReceipt(
            await executor.ExecuteAsync(envelope),
            FactionCrierExecutionDisposition.Committed,
            "daily claim");

        var duplicate = RequireReceipt(
            await executor.ExecuteAsync(envelope),
            FactionCrierExecutionDisposition.Duplicate,
            "daily command duplicate");
        AssertSameReceipt(committed, duplicate, "daily duplicate replay");
        var explicitReplay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                fixture.Ownership,
                new FactionCrierReplayIntent(fixture.RealmId, 1),
                identity),
            FactionCrierExecutionDisposition.Duplicate,
            "daily replay intent");
        AssertSameReceipt(committed, explicitReplay, "daily explicit replay");

        var secondOperation = CreateEnvelope(
            fixture,
            FactionCrierOperationIdentity.SecureClient(Guid.NewGuid()),
            subId: 1,
            realmPeriodDayNumber: day.DayNumber);
        Check.Equal(
            (int)FactionCrierExecutionDisposition.AlreadyClaimed,
            (int)(await executor.ExecuteAsync(secondOperation)).Disposition,
            "distinct daily operation is fenced by durable day uniqueness");

        var conflict = CreateEnvelope(
            fixture,
            identity,
            subId: 1,
            realmPeriodDayNumber: day.DayNumber + 1);
        Check.Equal(
            (int)FactionCrierExecutionDisposition.RequestHashConflict,
            (int)(await executor.ExecuteAsync(conflict)).Disposition,
            "same operation identity with changed request is rejected");

        var state = await ReadStateAsync(dataSource, fixture);
        AssertCommitAuthority(
            state,
            committed,
            expectedInventoryLedgerCount: 1,
            expectedCurrencyCode: string.Empty,
            expectedCurrencyDelta: 0,
            dailyCount: 1,
            weeklyCount: 0,
            exchangeCount: 0,
            "daily claim");
        Check.True(
            committed.WalletRevision == 0 &&
            committed.InventoryRevision == 1 &&
            committed.ProgressionRevision == 0 &&
            committed.FactionCrierRevision == 1 &&
            state.DuplicateCount == 2 &&
            state.RequestConflictCount == 1 &&
            state.Silver == 0 && state.Gold == 0 &&
            state.BindingGold == 0,
            "daily replays and conflict do not repeat mutation");
        var plate = await ReadNameplateAsync(dataSource, fixture, 3820);
        Check.True(
            plate == new NameplateState(1, 1),
            "Monday grants exactly one Nameplate I");
        await AssertDailySettlementAsync(
            dataSource,
            fixture,
            committed,
            balance.Revision,
            day);
        await AssertStoredReceiptTamperRejectedAsync(dataSource, fixture);
        await AssertDailyEvidenceGuardsAsync(dataSource, fixture);
    }

    private static async Task AssertWeeklyGoldCommitAsync(
        NpgsqlDataSource dataSource,
        PostgresFactionCrierCommandExecutor executor,
        FactionCrierBalanceSnapshot balance)
    {
        var fixture = await CreateFixtureAsync(
            dataSource,
            "weekly",
            silver: 0,
            gold: 1_000,
            bindingGold: 0);
        var day = TestRealmCalendar.GetDay(ReviewedMondayInstant);
        var weekStart = RealmCalendar.GetWeekStart(day);
        var receipt = RequireReceipt(
            await executor.ExecuteAsync(CreateEnvelope(
                fixture,
                FactionCrierOperationIdentity.SecureClient(Guid.NewGuid()),
                subId: 31,
                realmPeriodDayNumber: weekStart.DayNumber)),
            FactionCrierExecutionDisposition.Committed,
            "weekly reclaim");
        var state = await ReadStateAsync(dataSource, fixture);
        AssertCommitAuthority(
            state,
            receipt,
            expectedInventoryLedgerCount: 1,
            expectedCurrencyCode: "gold",
            expectedCurrencyDelta: -balance.WeeklyReclaimGoldCost,
            dailyCount: 0,
            weeklyCount: 1,
            exchangeCount: 0,
            "weekly reclaim");
        Check.True(
            receipt.WalletRevision == 1 &&
            receipt.InventoryRevision == 1 &&
            receipt.ProgressionRevision == 0 &&
            receipt.FactionCrierRevision == 1 &&
            state.Gold == 1_000 - balance.WeeklyReclaimGoldCost,
            "weekly reclaim debits exact Gold once");
        Check.True(
            await ReadNameplateAsync(dataSource, fixture, 3820) ==
                new NameplateState(1, 1),
            "weekly selection grants exactly one requested Nameplate");
        await AssertWeeklySettlementAsync(
            dataSource,
            fixture,
            receipt,
            balance.Revision,
            balance.WeeklyReclaimGoldCost,
            weekStart);
        await AssertWeeklyEvidenceGuardAsync(dataSource, fixture);
    }

    private static void AssertSameReceipt(
        FactionCrierExecutionReceipt expected,
        FactionCrierExecutionReceipt actual,
        string description) =>
        Check.True(
            expected.CharacterId == actual.CharacterId &&
            expected.Operation == actual.Operation &&
            expected.SubId == actual.SubId &&
            expected.WalletRevision == actual.WalletRevision &&
            expected.InventoryRevision == actual.InventoryRevision &&
            expected.ProgressionRevision == actual.ProgressionRevision &&
            expected.FactionCrierRevision == actual.FactionCrierRevision &&
            expected.AuditId == actual.AuditId &&
            expected.EventId == actual.EventId,
            $"{description} retains exact durable receipt identity");
}
