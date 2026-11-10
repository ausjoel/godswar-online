using System.Diagnostics;
using Godswar.Server.Application.Reconciliation;
using Npgsql;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal enum ReconciliationWorkerState : byte
{
    Disabled = 0,
    Starting = 1,
    Running = 2,
    Stopped = 3,
    Faulted = 4
}

internal readonly record struct ReconciliationWorkerSnapshot(
    bool Enabled,
    ReconciliationWorkerState State,
    bool FirstPassCompleted,
    TimeSpan HeartbeatAge,
    TimeSpan MaximumHealthyHeartbeatAge,
    ReconciliationRunStatus? LastRunStatus,
    long LastFindingCount,
    bool LastRunTruncated,
    bool HealthyBatchCompleted,
    TimeSpan SweepAge)
{
    public bool IsReady => !Enabled ||
        (State == ReconciliationWorkerState.Running &&
         HealthyBatchCompleted &&
         HeartbeatAge <= MaximumHealthyHeartbeatAge);
}

internal sealed class PostgresReconciliationWorker
{
    private readonly ReconciliationRunner _runner;
    private readonly ReconciliationOptions _options;
    private readonly ReconciliationMetrics _metrics;
    private readonly object _gate = new();
    private ReconciliationWorkerState _state;
    private bool _firstPassCompleted;
    private bool _healthyBatchCompleted;
    private long _lastSweepTimestamp;
    private long _lastHeartbeatTimestamp;
    private ReconciliationRunStatus? _lastRunStatus;
    private long _lastFindingCount;
    private bool _lastRunTruncated;

    public PostgresReconciliationWorker(
        ReconciliationRunner runner,
        ReconciliationOptions options,
        ReconciliationMetrics? metrics = null)
    {
        _runner = runner ??
            throw new ArgumentNullException(nameof(runner));
        _options = options ??
            throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _metrics = metrics ?? new ReconciliationMetrics();
        _state = options.Enabled
            ? ReconciliationWorkerState.Starting
            : ReconciliationWorkerState.Disabled;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        SetState(ReconciliationWorkerState.Running);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextDelay = _options.PollInterval;
                try
                {
                    var report =
                        await _runner.RunScheduledAsync(
                            cancellationToken);
                    Record(report);
                    nextDelay = NextDelay(report);
                }
                catch (NpgsqlException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    RecordUnhealthy();
                    _metrics.RecordWorkerFailure(
                        "database_unavailable");
                }
                catch (TimeoutException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    RecordUnhealthy();
                    _metrics.RecordWorkerFailure(
                        "database_timeout");
                }

                await Task.Delay(
                    nextDelay,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            SetState(ReconciliationWorkerState.Stopped);
        }
        catch
        {
            SetState(ReconciliationWorkerState.Faulted);
            throw;
        }
    }

    public ReconciliationWorkerSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var age = _lastHeartbeatTimestamp == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(
                    _lastHeartbeatTimestamp);
            return new ReconciliationWorkerSnapshot(
                _options.Enabled,
                _state,
                _firstPassCompleted,
                age,
                _options.PollInterval +
                    _options.RunTimeout +
                    TimeSpan.FromSeconds(15),
                _lastRunStatus,
                _lastFindingCount,
                _lastRunTruncated,
                _healthyBatchCompleted,
                _lastSweepTimestamp == 0
                    ? TimeSpan.MaxValue
                    : Stopwatch.GetElapsedTime(_lastSweepTimestamp));
        }
    }

    private void Record(ReconciliationReport report)
    {
        lock (_gate)
        {
            _lastRunStatus = report.Status;
            _lastFindingCount = report.Findings.Sum(
                finding => finding.Count);
            _lastRunTruncated = report.Truncated;
            _healthyBatchCompleted = IsHealthyBatch(report);
            if (_healthyBatchCompleted)
            {
                _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
            }
            if (report.Status == ReconciliationRunStatus.Completed)
            {
                _firstPassCompleted = true;
                _lastSweepTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    internal TimeSpan NextDelay(ReconciliationReport report) =>
        report.Status == ReconciliationRunStatus.Truncated &&
            IsHealthyBatch(report)
            ? TimeSpan.FromSeconds(1)
            : _options.PollInterval;

    private static bool IsHealthyBatch(ReconciliationReport report) =>
        report.AuthorityValidated &&
        report.Status is ReconciliationRunStatus.Completed
            or ReconciliationRunStatus.Truncated;

    private void RecordUnhealthy()
    {
        lock (_gate)
        {
            _healthyBatchCompleted = false;
        }
    }

    private void SetState(ReconciliationWorkerState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }
}
