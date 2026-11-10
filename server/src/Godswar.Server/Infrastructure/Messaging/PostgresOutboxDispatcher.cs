using System.Diagnostics;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Operations.Observability;
using Npgsql;

namespace Godswar.Server.Infrastructure.Messaging;

internal sealed partial class PostgresOutboxDispatcher
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyDictionary<string, RegisteredConsumer> _consumers;
    private readonly string[] _consumerKeys;
    private readonly string[] _orderingPolicies;
    private readonly PostgresOutboxDispatcherOptions _options;
    private readonly string _leaseOwner;
    private readonly int _commandTimeoutSeconds;
    private readonly IPostgresOutboxDispatcherProbe? _probe;

    public PostgresOutboxDispatcher(
        NpgsqlDataSource dataSource,
        IEnumerable<IOutboxEventConsumer> consumers,
        PostgresOutboxDispatcherOptions options,
        string? leaseOwner = null,
        IPostgresOutboxDispatcherProbe? probe = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _consumers = BuildConsumerRegistry(consumers);
        _consumerKeys = _consumers.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        _orderingPolicies = _consumerKeys
            .Select(key => _consumers[key].DatabaseOrderingPolicy)
            .ToArray();
        _options = options;
        _leaseOwner = RequireLeaseOwner(
            leaseOwner ?? $"outbox-{Guid.NewGuid():N}");
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _probe = probe;
    }

    public Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        return RunCoreAsync(DispatchOnceAsync, cancellationToken);
    }

    internal Task RunAsync(
        Func<CancellationToken, Task<int>> dispatchPass,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatchPass);
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        return RunCoreAsync(dispatchPass, cancellationToken);
    }

    private async Task RunCoreAsync(
        Func<CancellationToken, Task<int>> dispatchPass,
        CancellationToken cancellationToken)
    {
        PostgresCommandMetrics.MarkOutboxStarted();
        var consecutiveTransientFailures = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextDelay = _options.PollInterval;
                try
                {
                    await dispatchPass(cancellationToken);
                    consecutiveTransientFailures = 0;
                    PostgresCommandMetrics.MarkOutboxPassCompleted();
                }
                catch (NpgsqlException error)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    if (!error.IsTransient)
                    {
                        throw;
                    }

                    consecutiveTransientFailures = Math.Min(
                        consecutiveTransientFailures + 1,
                        30);
                    PostgresCommandMetrics.RecordRetry(
                        "dispatcher",
                        "database_unavailable");
                    nextDelay = FailureRetryDelay(
                        consecutiveTransientFailures);
                }
                catch (TimeoutException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    consecutiveTransientFailures = Math.Min(
                        consecutiveTransientFailures + 1,
                        30);
                    PostgresCommandMetrics.RecordRetry(
                        "dispatcher",
                        "database_timeout");
                    nextDelay = FailureRetryDelay(
                        consecutiveTransientFailures);
                }
                await Task.Delay(
                    nextDelay,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown is a successful dispatcher stop. A claim that
            // was interrupted remains durably leased and is recovered after
            // expiry by the next process.
            PostgresCommandMetrics.MarkOutboxStopped();
        }
        catch
        {
            PostgresCommandMetrics.MarkOutboxFaulted();
            throw;
        }
    }

    private TimeSpan FailureRetryDelay(int consecutiveFailures)
    {
        var retryDelay = _options.RetryDelay(consecutiveFailures);
        return retryDelay > _options.PollInterval
            ? retryDelay
            : _options.PollInterval;
    }

    internal async Task<int> DispatchOnceAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        var started = Stopwatch.GetTimestamp();
        using var activity = ServerActivity.Start(
            ServerTraceOperation.OutboxDispatch,
            ActivityKind.Client,
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Component,
                "outbox"));
        try
        {
            var processed = 0;
            var performPassValidation = true;
            while (processed < _options.BatchSize)
            {
                // Lease only the work that will be consumed immediately.
                // Reserving a whole sequential batch starts every lease clock
                // at once and can make later events expire while they wait
                // behind a slow consumer.
                var batch = await ClaimBatchAsync(
                    performPassValidation,
                    cancellationToken);
                // This heartbeat follows a completed database transaction.
                // It never pulses while a database command or consumer is
                // stalled, so readiness can still detect a wedged worker.
                PostgresCommandMetrics.MarkOutboxProgress();
                performPassValidation = false;
                RecordDeferredOutcomes(batch.DeferredOutcomes);
                processed += batch.DeferredOutcomes.Count;

                if (batch.DeferredOutcomes.Any(
                        static outcome =>
                            outcome.Kind == DeferredOutcomeKind.Gap))
                {
                    // A strict gap is a backoff boundary, not useful batch
                    // throughput. End this pass so a permanently blocked
                    // stream cannot consume the remaining batch or be
                    // revisited after its delay expires within this pass.
                    break;
                }

                if (batch.Claims.Count == 0)
                {
                    if (batch.DeferredOutcomes.Count == 0)
                    {
                        break;
                    }

                    continue;
                }

                await ReachAsync(
                    PostgresOutboxDispatcherProbeStage.AfterClaim,
                    cancellationToken);
                await DispatchClaimAsync(
                    batch.Claims[0],
                    cancellationToken);
                processed++;
                PostgresCommandMetrics.MarkOutboxProgress();
            }

            await RefreshBacklogAsync(cancellationToken);
            ServerActivity.Complete(
                activity,
                ServerTraceOutcome.Accepted);
            return processed;
        }
        catch (OperationCanceledException)
        {
            ServerActivity.Complete(
                activity,
                ServerTraceOutcome.Cancelled);
            throw;
        }
        catch
        {
            ServerActivity.Complete(
                activity,
                ServerTraceOutcome.Faulted);
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordDispatchDuration(
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task DispatchClaimAsync(
        ClaimedEvent claim,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(_options.CommandTimeout);

        try
        {
            await claim.Consumer.ConsumeAsync(
                claim.Message,
                timeout.Token);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // A shutdown is crash-equivalent. Keep the durable lease so a
            // later poller recovers it through the normal expiry path.
            throw;
        }
        catch (OperationCanceledException)
        {
            var disposition = await CompleteFailureAsync(
                claim,
                "consumer_timeout",
                cancellationToken);
            RecordFailureDisposition(
                claim.Message.ConsumerKey,
                "consumer_timeout",
                disposition);
            return;
        }
        catch (Exception)
        {
            var disposition = await CompleteFailureAsync(
                claim,
                "consumer_failure",
                cancellationToken);
            RecordFailureDisposition(
                claim.Message.ConsumerKey,
                "consumer_failure",
                disposition);
            return;
        }

        // This probe intentionally sits outside the consumer catch block.
        // Throwing here simulates a process crash after the side effect but
        // before its durable checkpoint.
        await ReachAsync(
            PostgresOutboxDispatcherProbeStage.AfterConsumerSuccess,
            cancellationToken);

        var result = await CompleteSuccessAsync(
            claim,
            cancellationToken);
        PostgresCommandMetrics.RecordOutbox(
            claim.Message.ConsumerKey,
            result == CompletionDisposition.Delivered
                ? "delivered"
                : "lease_lost");
    }

    private async ValueTask ReachAsync(
        PostgresOutboxDispatcherProbeStage stage,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, cancellationToken);
        }
    }

    private static void RecordDeferredOutcomes(
        IReadOnlyList<DeferredOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            switch (outcome.Kind)
            {
                case DeferredOutcomeKind.Stale:
                    PostgresCommandMetrics.RecordOutbox(
                        outcome.ConsumerKey,
                        "stale");
                    break;
                case DeferredOutcomeKind.Gap:
                    PostgresCommandMetrics.RecordGap(
                        outcome.ConsumerKey);
                    PostgresCommandMetrics.RecordOutbox(
                        outcome.ConsumerKey,
                        "gap_delayed");
                    break;
                case DeferredOutcomeKind.LeaseExpiredRetry:
                    PostgresCommandMetrics.RecordRetry(
                        outcome.ConsumerKey,
                        "lease_expired");
                    break;
                case DeferredOutcomeKind.LeaseExpiredPoison:
                    PostgresCommandMetrics.RecordPoison(
                        outcome.ConsumerKey,
                        "lease_expired");
                    break;
                case DeferredOutcomeKind.AttemptsExhaustedPoison:
                    PostgresCommandMetrics.RecordPoison(
                        outcome.ConsumerKey,
                        "attempts_exhausted");
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unknown deferred outbox outcome.");
            }
        }
    }

    private static void RecordFailureDisposition(
        string consumerKey,
        string reason,
        CompletionDisposition disposition)
    {
        switch (disposition)
        {
            case CompletionDisposition.RetryScheduled:
                PostgresCommandMetrics.RecordRetry(
                    consumerKey,
                    reason);
                break;
            case CompletionDisposition.Poisoned:
                PostgresCommandMetrics.RecordPoison(
                    consumerKey,
                    reason);
                break;
            case CompletionDisposition.LeaseLost:
                PostgresCommandMetrics.RecordOutbox(
                    consumerKey,
                    "lease_lost");
                break;
            default:
                throw new InvalidOperationException(
                    "Unexpected failed-delivery disposition.");
        }
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };
}
