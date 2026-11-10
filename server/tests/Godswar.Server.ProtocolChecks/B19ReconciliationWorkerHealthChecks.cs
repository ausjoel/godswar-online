using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Reconciliation;

namespace Godswar.Server.ProtocolChecks;

internal static partial class B19ReconciliationWorkerChecks
{
    private static async Task CheckAuthorityMismatchReadinessAsync()
    {
        foreach (var category in new[]
                 {
                     ReconciliationCategory.SchemaMigrationManifestMismatch,
                     ReconciliationCategory.NpcContentPublicationMismatch,
                     ReconciliationCategory.NpcContentCountMismatch
                 })
        {
            var options = Options(enabled: true);
            var worker = new PostgresReconciliationWorker(
                new ReconciliationRunner(new ManifestMismatchReader(category),
                    options), options);
            using var shutdown = new CancellationTokenSource();
            var run = worker.RunAsync(shutdown.Token);
            var snapshot = await WaitForSnapshotAsync(worker,
                value => value.LastRunStatus is not null);
            Check.True(!snapshot.IsReady && !snapshot.HealthyBatchCompleted &&
                    snapshot.HeartbeatAge == TimeSpan.MaxValue,
                $"{category} cannot establish readiness or a healthy heartbeat");
            shutdown.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static void CheckContinuationDelay()
    {
        var options = Options(enabled: true);
        var worker = new PostgresReconciliationWorker(
            new ReconciliationRunner(new TerminalReader(), options), options);
        var healthy = new ReconciliationReport(1, ReconciliationMode.ReportOnly,
            ReconciliationRunStatus.Truncated, DateTimeOffset.UnixEpoch,
            0, 1, 0, true, [], AuthorityValidated: true);
        Check.True(worker.NextDelay(healthy) == TimeSpan.FromSeconds(1),
            "healthy truncated work continues after one bounded second");
        Check.True(worker.NextDelay(healthy with { AuthorityValidated = false }) ==
                options.PollInterval &&
            worker.NextDelay(healthy with { Status = ReconciliationRunStatus.TimedOut }) ==
                options.PollInterval &&
            worker.NextDelay(healthy with { Status = ReconciliationRunStatus.Completed }) ==
                options.PollInterval,
            "mismatch, timeout and completed sweeps retain normal polling");

        var snapshot = new ReconciliationWorkerSnapshot(true,
            ReconciliationWorkerState.Running, false, TimeSpan.Zero,
            TimeSpan.FromSeconds(20), ReconciliationRunStatus.Truncated,
            0, true, true, TimeSpan.MaxValue);
        Check.True(snapshot.IsReady &&
                !(snapshot with { HealthyBatchCompleted = false }).IsReady &&
                !(snapshot with { HeartbeatAge = TimeSpan.FromSeconds(21) }).IsReady &&
                !(snapshot with { State = ReconciliationWorkerState.Stopped }).IsReady,
            "readiness uses bounded health and freshness independently of sweep age");
    }

    private sealed class ManifestMismatchReader(ReconciliationCategory category)
        : IReconciliationReader
    {
        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout, CancellationToken cancellationToken) =>
            Task.FromResult<IReconciliationSnapshot>(new ManifestMismatchSnapshot(category));
    }

    private sealed class ManifestMismatchSnapshot(ReconciliationCategory category)
        : IReconciliationSnapshot
    {
        private readonly TerminalSnapshot _terminal = new();
        public Task<ReconciliationPage> ReadCharacterPageAsync(long afterCharacterKey,
            int limit, CancellationToken cancellationToken) =>
            _terminal.ReadCharacterPageAsync(afterCharacterKey, limit, cancellationToken);
        public Task<ReconciliationPage> ReadOutboxPageAsync(long afterOutboxKey,
            int limit, CancellationToken cancellationToken) =>
            _terminal.ReadOutboxPageAsync(afterOutboxKey, limit, cancellationToken);
        public Task<ReconciliationOutboxPositionPage> ReadOutboxPositionPageAsync(
            ReconciliationOutboxPositionCursor after, int limit,
            CancellationToken cancellationToken) =>
            _terminal.ReadOutboxPositionPageAsync(after, limit, cancellationToken);
        public Task<IReadOnlyList<ReconciliationCategoryCount>> ReadManifestAndContentAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([new(category, 1)]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
