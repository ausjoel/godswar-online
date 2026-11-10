using System.Diagnostics.Metrics;
using Godswar.Server.Application.Reconciliation;

namespace Godswar.Server.ProtocolChecks;

internal static partial class B19ReconciliationRunnerChecks
{
    private static async Task CheckTimeoutPreservesCompletedPagesAsync()
    {
        foreach (var scope in Enum.GetValues<TimeoutProgressScope>())
        {
            foreach (var commandTimeout in new[] { false, true })
            {
                using var meter = new Meter($"B19.progress.{Guid.NewGuid():N}");
                using var listener = new MeterListener();
                long observedFindings = 0;
                listener.InstrumentPublished = (instrument, target) =>
                {
                    if (instrument.Meter == meter)
                    {
                        target.EnableMeasurementEvents(instrument);
                    }
                };
                listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
                {
                    if (instrument.Name == "godswar_reconciliation_findings_total")
                    {
                        Interlocked.Add(ref observedFindings, value);
                    }
                });
                listener.Start();
                var reader = new TimeoutProgressReader(scope, commandTimeout);
                var options = Options(batchSize: 1);
                options.CommandTimeoutMilliseconds = 100;
                options.RunTimeoutMilliseconds = 100;
                var runner = new ReconciliationRunner(reader, options,
                    new ReconciliationMetrics(meter));

                var timedOut = await runner.RunScheduledAsync();
                Check.True(timedOut.Status == ReconciliationRunStatus.TimedOut &&
                        timedOut.Truncated && timedOut.AuthorityValidated &&
                        timedOut.CharacterRowsScanned + timedOut.OutboxRowsScanned == 1 &&
                        FindCount(timedOut, ReconciliationCategory.OutboxSequenceGap) == 1,
                    $"{scope} timeout preserves completed page observations");
                var completed = await runner.RunScheduledAsync();
                Check.True(completed.Status == ReconciliationRunStatus.Completed &&
                        completed.CharacterRowsScanned + completed.OutboxRowsScanned == 1 &&
                        FindCount(completed, ReconciliationCategory.OutboxSequenceGap) == 3 &&
                        observedFindings == 3,
                    $"{scope} continuation retains findings without counting them twice");
                Check.True(reader.Requests.SequenceEqual(["start", "resume", "resume"]),
                    $"{scope} resumes at the unfinished page instead of rereading completed pages");
            }
        }
    }

    private enum TimeoutProgressScope { Characters, Events, Positions }

    private sealed class TimeoutProgressReader(
        TimeoutProgressScope scope, bool commandTimeout) : IReconciliationReader
    {
        private int _opens;
        public List<string> Requests { get; } = [];
        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeoutBound, CancellationToken cancellationToken) =>
            Task.FromResult<IReconciliationSnapshot>(new TimeoutProgressSnapshot(
                scope, commandTimeout, Interlocked.Increment(ref _opens) == 1,
                Requests));
    }

    private sealed class TimeoutProgressSnapshot(TimeoutProgressScope scope,
        bool commandTimeout, bool firstRun, List<string> requests)
        : IReconciliationSnapshot
    {
        private static readonly ReconciliationOutboxPositionCursor NextPosition =
            new("consumer", "character", "10");

        public Task<ReconciliationPage> ReadCharacterPageAsync(long afterCharacterKey,
            int limit, CancellationToken cancellationToken) =>
            scope == TimeoutProgressScope.Characters
                ? ReadPageAsync(afterCharacterKey, cancellationToken)
                : Task.FromResult(new ReconciliationPage(0, 0, true, []));

        public Task<ReconciliationPage> ReadOutboxPageAsync(long afterOutboxKey,
            int limit, CancellationToken cancellationToken) =>
            scope == TimeoutProgressScope.Events
                ? ReadPageAsync(afterOutboxKey, cancellationToken)
                : Task.FromResult(new ReconciliationPage(0, 0, true, []));

        private async Task<ReconciliationPage> ReadPageAsync(long cursor,
            CancellationToken cancellationToken)
        {
            requests.Add(cursor == 0 ? "start" : "resume");
            if (cursor == 0)
            {
                return new ReconciliationPage(10, 1, false,
                    [new(ReconciliationCategory.OutboxSequenceGap, 1)]);
            }
            await InterruptIfFirstRunAsync(cancellationToken);
            return new ReconciliationPage(20, 1, true,
                [new(ReconciliationCategory.OutboxSequenceGap, 2)]);
        }

        public async Task<ReconciliationOutboxPositionPage> ReadOutboxPositionPageAsync(
            ReconciliationOutboxPositionCursor after, int limit,
            CancellationToken cancellationToken)
        {
            if (scope != TimeoutProgressScope.Positions)
            {
                return new ReconciliationOutboxPositionPage(after, 0, true, []);
            }
            var atStart = after == ReconciliationOutboxPositionCursor.Start;
            requests.Add(atStart ? "start" : "resume");
            if (atStart)
            {
                return new ReconciliationOutboxPositionPage(NextPosition, 1, false,
                    [new(ReconciliationCategory.OutboxSequenceGap, 1)]);
            }
            await InterruptIfFirstRunAsync(cancellationToken);
            return new ReconciliationOutboxPositionPage(after, 1, true,
                [new(ReconciliationCategory.OutboxSequenceGap, 2)]);
        }

        private async Task InterruptIfFirstRunAsync(CancellationToken cancellationToken)
        {
            if (!firstRun) { return; }
            if (commandTimeout)
            {
                throw new InvalidOperationException("Provider command timed out.",
                    new TimeoutException("Expected bounded page timeout."));
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task<IReadOnlyList<ReconciliationCategoryCount>> ReadManifestAndContentAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
