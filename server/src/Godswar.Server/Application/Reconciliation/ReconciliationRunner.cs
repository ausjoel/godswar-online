using System.Diagnostics;

namespace Godswar.Server.Application.Reconciliation;

internal sealed partial class ReconciliationRunner
{
    private readonly IReconciliationReader _reader;
    private readonly ReconciliationOptions _options;
    private readonly ReconciliationMetrics _metrics;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private ReconciliationScanState _continuation =
        ReconciliationScanState.Start;

    public ReconciliationRunner(
        IReconciliationReader reader,
        ReconciliationOptions options,
        ReconciliationMetrics? metrics = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _options = options ??
            throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _metrics = metrics ?? new ReconciliationMetrics();
    }

    public async Task<ReconciliationReport> RunAsync(
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            return await RunExclusiveAsync(cancellationToken);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public Task<ReconciliationReport> RunScheduledAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(cancellationToken);

    private async Task<ReconciliationReport> RunExclusiveAsync(
        CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var manifestCounts =
            new Dictionary<ReconciliationCategory, long>();
        var progress = new ScanProgress(_continuation);
        var scanPerformed = false;
        var authorityValidated = false;
        var truncated = false;
        var timedOut = false;

        using var timeout =
            new CancellationTokenSource(_options.RunTimeout);
        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);

        try
        {
            await using var snapshot =
                await _reader.OpenSnapshotAsync(
                    _options.CommandTimeout,
                    linked.Token);
            var manifest = await snapshot
                .ReadManifestAndContentAsync(linked.Token);
            Add(manifestCounts, manifest);
            authorityValidated = !manifest.Any(finding =>
                finding.Count > 0 && finding.Category is
                    ReconciliationCategory.SchemaMigrationManifestMismatch or
                    ReconciliationCategory.NpcContentPublicationMismatch or
                    ReconciliationCategory.NpcContentCountMismatch);
            var schemaMismatch = manifest.Any(finding =>
                finding.Category ==
                    ReconciliationCategory
                        .SchemaMigrationManifestMismatch &&
                finding.Count > 0);
            if (!schemaMismatch)
            {
                scanPerformed = true;
                await ScanCharactersAsync(snapshot, progress, linked.Token);
                await ScanOutboxAsync(snapshot, progress, linked.Token);
                truncated = !progress.State.SweepCompleted;
            }
            else
            {
                // The authoritative schema is not safe to inspect yet.
                // Preserve the last committed cursors and never claim that
                // the logical character/outbox sweep completed.
                truncated = true;
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested &&
                  timeout.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (Exception exception)
            when (!cancellationToken.IsCancellationRequested &&
                  (exception is TimeoutException ||
                   exception.InnerException is TimeoutException))
        {
            // Providers may surface command timeouts before the run deadline.
            // Already completed pages remain valid report-only observations.
            timedOut = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var status = timedOut
            ? ReconciliationRunStatus.TimedOut
            : truncated
                ? ReconciliationRunStatus.Truncated
                : ReconciliationRunStatus.Completed;
        var accumulatedCounts =
            ToDictionary(_continuation.AccumulatedFindings);
        Add(accumulatedCounts, ToCounts(progress.Counts));
        var reportCounts =
            new Dictionary<ReconciliationCategory, long>(
                accumulatedCounts);
        Add(reportCounts, ToCounts(manifestCounts));
        var observedCounts =
            new Dictionary<ReconciliationCategory, long>(
                progress.Counts);
        Add(observedCounts, ToCounts(manifestCounts));
        var report = new ReconciliationReport(
            SchemaVersion: 1,
            ReconciliationMode.ReportOnly,
            status,
            startedAtUtc,
            Math.Max(0, stopwatch.ElapsedMilliseconds),
            progress.CharacterRows,
            progress.OutboxRows,
            truncated || timedOut,
            ToCounts(reportCounts),
            authorityValidated);
        _metrics.Record(report, ToCounts(observedCounts));
        if (scanPerformed)
        {
            _continuation =
                status == ReconciliationRunStatus.Completed
                    ? ReconciliationScanState.Start
                    : progress.State with
                    {
                        AccumulatedFindings =
                            ToCounts(accumulatedCounts)
                    };
        }

        return report;
    }

    private static void Add(
        IDictionary<ReconciliationCategory, long> target,
        IEnumerable<ReconciliationCategoryCount> additions)
    {
        foreach (var addition in additions)
        {
            if (!Enum.IsDefined(addition.Category) ||
                addition.Count < 0)
            {
                throw new InvalidDataException(
                    "A reconciliation count cannot be negative.");
            }

            target.TryGetValue(addition.Category, out var current);
            target[addition.Category] =
                checked(current + addition.Count);
        }
    }

    private static Dictionary<ReconciliationCategory, long>
        ToDictionary(
            IEnumerable<ReconciliationCategoryCount> counts)
    {
        var result = new Dictionary<ReconciliationCategory, long>();
        Add(result, counts);
        return result;
    }

    private static IReadOnlyList<ReconciliationCategoryCount> ToCounts(
        IDictionary<ReconciliationCategory, long> counts) =>
        counts
            .Where(pair => pair.Value > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => new ReconciliationCategoryCount(
                pair.Key,
                pair.Value))
            .ToArray();
}
