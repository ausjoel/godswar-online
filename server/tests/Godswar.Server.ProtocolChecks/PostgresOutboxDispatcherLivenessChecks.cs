using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Operations;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresOutboxDispatcherLivenessChecks
{
    public const string CheckName =
        "Outbox dispatcher fail-closed liveness";

    public static async Task RunAsync()
    {
        CheckReadinessStates();
        CheckDatabaseErrorClassification();
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;" +
            "Username=unused;Password=unused;Timeout=1");
        await CheckFailedPassDoesNotHeartbeatAsync(dataSource);
        await CheckDeterministicErrorFaultsAsync(dataSource);
        CheckTerminalStatesRejectHeartbeat();
    }

    private static void CheckReadinessStates()
    {
        var fresh = TimeSpan.FromSeconds(1);
        var stale = TimeSpan.FromSeconds(16);
        var maximumAge = TimeSpan.FromSeconds(15);

        Check.True(
            ServerReadinessMonitor.IsOutboxReady(
                required: false,
                Snapshot(OutboxDispatcherState.NotStarted, stale),
                maximumAge),
            "disabled outbox does not gate readiness");
        Check.True(
            ServerReadinessMonitor.IsOutboxReady(
                required: true,
                Snapshot(OutboxDispatcherState.Running, fresh),
                maximumAge),
            "running outbox with a fresh heartbeat is ready");
        Check.True(
            !ServerReadinessMonitor.IsOutboxReady(
                required: true,
                Snapshot(OutboxDispatcherState.Running, stale),
                maximumAge),
            "running outbox with a stale heartbeat fails readiness");
        Check.True(
            !ServerReadinessMonitor.IsOutboxReady(
                required: true,
                Snapshot(OutboxDispatcherState.Stopped, fresh),
                maximumAge),
            "stopped outbox fails readiness");
        Check.True(
            !ServerReadinessMonitor.IsOutboxReady(
                required: true,
                Snapshot(OutboxDispatcherState.Faulted, fresh),
                maximumAge),
            "faulted outbox fails readiness");
    }

    private static void CheckDatabaseErrorClassification()
    {
        var unavailable = new PostgresException(
            "database is starting",
            "FATAL",
            "FATAL",
            "57P03");
        var missingTable = new PostgresException(
            "relation does not exist",
            "ERROR",
            "ERROR",
            "42P01");
        var denied = new PostgresException(
            "permission denied",
            "ERROR",
            "ERROR",
            "42501");

        Check.True(
            unavailable.IsTransient,
            "PostgreSQL startup unavailability is retryable");
        Check.True(
            !missingTable.IsTransient,
            "missing outbox schema is deterministic");
        Check.True(
            !denied.IsTransient,
            "outbox permission denial is deterministic");
    }

    private static async Task CheckFailedPassDoesNotHeartbeatAsync(
        NpgsqlDataSource dataSource)
    {
        var dispatcher = CreateDispatcher(dataSource);
        var repeatedFailuresReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pass = 0;
        var failures = 0;
        using var shutdown = new CancellationTokenSource();
        var run = dispatcher.RunAsync(
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref pass) == 1)
                {
                    return 0;
                }

                await Task.Delay(75, cancellationToken);
                if (Interlocked.Increment(ref failures) == 3)
                {
                    repeatedFailuresReached.TrySetResult();
                }
                throw new NpgsqlException(
                    "simulated transient connection failure",
                    new TimeoutException());
            },
            shutdown.Token);

        await repeatedFailuresReached.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await Task.Delay(25);
        var failed = PostgresCommandMetrics.GetSnapshot();
        Check.True(
            failed.State == OutboxDispatcherState.Running,
            "transient database failure keeps the retry worker running");
        Check.True(
            failed.HeartbeatAge >= TimeSpan.FromMilliseconds(300),
            "repeated failed passes age the successful-pass heartbeat");

        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(
            PostgresCommandMetrics.GetSnapshot().State ==
                OutboxDispatcherState.Stopped,
            "cancelling a retrying outbox worker stops cleanly");
    }

    private static async Task CheckDeterministicErrorFaultsAsync(
        NpgsqlDataSource dataSource)
    {
        var dispatcher = CreateDispatcher(dataSource);
        var attempts = 0;
        var error = new PostgresException(
            "relation outbox_events does not exist",
            "ERROR",
            "ERROR",
            "42P01");

        try
        {
            await dispatcher.RunAsync(
                _ =>
                {
                    Interlocked.Increment(ref attempts);
                    return Task.FromException<int>(error);
                });
            throw new InvalidOperationException(
                "Expected deterministic outbox error was not raised.");
        }
        catch (PostgresException actual) when (ReferenceEquals(actual, error))
        {
        }

        Check.Equal(
            1,
            attempts,
            "deterministic database error is not retried");
        Check.True(
            PostgresCommandMetrics.GetSnapshot().State ==
                OutboxDispatcherState.Faulted,
            "deterministic database error faults the dispatcher");
    }

    private static void CheckTerminalStatesRejectHeartbeat()
    {
        var faultedAge = PostgresCommandMetrics.GetSnapshot().HeartbeatAge;
        Thread.Sleep(20);
        PostgresCommandMetrics.MarkOutboxProgress();
        PostgresCommandMetrics.MarkOutboxPassCompleted();
        var afterFaultedPass = PostgresCommandMetrics.GetSnapshot();
        Check.True(
            afterFaultedPass.State == OutboxDispatcherState.Faulted,
            "pass markers cannot revive a faulted dispatcher");
        Check.True(
            afterFaultedPass.HeartbeatAge > faultedAge,
            "pass markers cannot refresh a faulted dispatcher");

        PostgresCommandMetrics.MarkOutboxStopped();
        Thread.Sleep(20);
        var stoppedAge = PostgresCommandMetrics.GetSnapshot().HeartbeatAge;
        PostgresCommandMetrics.MarkOutboxProgress();
        PostgresCommandMetrics.MarkOutboxPassCompleted();
        var afterStoppedPass = PostgresCommandMetrics.GetSnapshot();
        Check.True(
            afterStoppedPass.State == OutboxDispatcherState.Stopped,
            "pass markers cannot revive a stopped dispatcher");
        Check.True(
            afterStoppedPass.HeartbeatAge >= stoppedAge,
            "pass markers cannot refresh a stopped dispatcher");

        // Restore the process-global test metric to the state established by
        // the existing persistence-worker checks.
        PostgresCommandMetrics.MarkOutboxStarted();
    }

    private static PostgresOutboxRuntimeSnapshot Snapshot(
        OutboxDispatcherState state,
        TimeSpan heartbeatAge) =>
        new(state, 0, TimeSpan.Zero, heartbeatAge);

    private static PostgresOutboxDispatcher CreateDispatcher(
        NpgsqlDataSource dataSource) =>
        new(
            dataSource,
            [new NoopConsumer()],
            new PostgresOutboxDispatcherOptions
            {
                BatchSize = 1,
                PollIntervalMilliseconds = 50,
                LeaseMilliseconds = 1_000,
                MaximumDeliveryAttempts = 1,
                BaseRetryDelayMilliseconds = 50,
                MaximumRetryDelayMilliseconds = 50,
                GapRetryDelayMilliseconds = 50,
                CommandTimeoutMilliseconds = 100
            });

    private sealed class NoopConsumer : IOutboxEventConsumer
    {
        public string ConsumerKey => "checks.outbox.liveness";

        public OutboxOrderingPolicy OrderingPolicy =>
            OutboxOrderingPolicy.StrictSequence;

        public ValueTask ConsumeAsync(
            OutboxEventMessage message,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
