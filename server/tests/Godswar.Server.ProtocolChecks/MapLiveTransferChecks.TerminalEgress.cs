using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MapLiveTransferChecks
{
    public const string TerminalEgressCheckName =
        "World membership terminal egress delivery drain";

    public static async Task RunTerminalEgressAsync()
    {
        await CheckLeasedTerminalEgressAsync(overflowFromLease: false);
        await CheckLeasedTerminalEgressAsync(overflowFromLease: true);
        await CheckTerminalReadinessBeforeRemovalAsync();
        await CheckDisposalOwnsUnscheduledTerminalCleanupAsync();
    }

    private static async Task CheckLeasedTerminalEgressAsync(bool overflowFromLease)
    {
        await using var registry = CreateRegistry();
        var transport = new TerminalFailureTransport();
        var options = new NetworkRuntimeOptions
        {
            ReliableEgressQueueItems = 1,
            ReliableEgressQueueBytes = LegacyProtocolLimits.MaxPacketLength,
            ReliableWriteTimeoutMilliseconds = 10_000
        };
        var session = new ClientSession(transport, options, NetworkEndpointRole.Game);
        var character = CreateCharacter();
        GameHandlerOwnershipTestFences.Bind(registry, session, AccountId, character);
        registry.JoinMap(session, AccountId, character, PlayerObjectId,
            worldReady: true, joinedAt: TestTime);
        registry.InitializeMapMonsters(SourceMapId, [CreateMonster()], TestTime);
        var runtime = GetMutationRuntime(registry,
            registry.GetMapSessions(SourceMapId).Single().WorldInstanceId);
        await using (var visibility = await registry.BeginMonsterVisibilityTransitionAsync(
                         session, SourceMapId, SourceX, SourceZ, CancellationToken.None))
        {
            visibility!.Commit();
        }

        using var rescue = new CancellationTokenSource();
        MonsterViewerDeliveryLease? lease = null;
        var sends = new List<Task>();
        try
        {
            var packet = PacketBuilder.PlayerManaUpdate(PlayerObjectId, character.CurrentMp);
            sends.Add(session.SendAsync(packet, rescue.Token));
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (overflowFromLease)
            {
                sends.Add(session.SendAsync(packet, rescue.Token));
                await WaitForTerminalQueueAsync(session);
            }

            lease = await runtime.Map.AcquireMonsterViewerDeliveryLeaseAsync(
                session, MonsterObjectId, CancellationToken.None);
            Check.True(lease is not null, "terminal fixture owns a source monster delivery lease");
            var retainedLease = lease!;
            var leasedSend = Task.Run(async () =>
            {
                await using (retainedLease)
                {
                    await session.SendAsync(overflowFromLease
                            ? new byte[options.ReliableEgressQueueBytes + 1]
                            : packet,
                        rescue.Token);
                }
            });
            sends.Add(leasedSend);
            if (!overflowFromLease)
            {
                await WaitForTerminalQueueAsync(session);
                transport.FailActiveWrite();
            }

            foreach (var send in sends)
            {
                await AssertTerminalSendFailureAsync(send);
            }
            using var terminalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!session.IsDisconnected || transport.DisconnectCount != 1)
            {
                await Task.Delay(5, terminalDeadline.Token);
            }
            Check.True(session.IsDisconnected && transport.DisconnectCount == 1,
                "terminal egress closes logical and physical session exactly once");
        }
        finally
        {
            // Release explicitly on assertion failure so the regression can
            // diagnose the old deadlock without leaving a blocked cleanup task.
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
            rescue.Cancel();
            try
            {
                await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
            }
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        Check.True(registry.GetMapPopulation(SourceMapId) == 0 &&
            !registry.TryGetSessionWorldInstanceId(session, out _),
            overflowFromLease
                ? "caller-owned overflow lease unwinds before tracked terminal world removal"
                : "queued delivery settles before tracked terminal world removal");
    }

    private static async Task CheckTerminalReadinessBeforeRemovalAsync()
    {
        await using var registry = CreateRegistry();
        var transport = new TerminalFailureTransport();
        var session = new ClientSession(transport);
        var character = CreateCharacter();
        GameHandlerOwnershipTestFences.Bind(registry, session, AccountId, character);
        registry.JoinMap(session, AccountId, character, PlayerObjectId,
            worldReady: true, joinedAt: TestTime);
        registry.InitializeMapMonsters(SourceMapId, [CreateMonster()], TestTime);
        var source = registry.GetMapSessions(SourceMapId).Single();
        var runtime = GetMutationRuntime(registry, source.WorldInstanceId);
        await using (var visibility = await registry.BeginMonsterVisibilityTransitionAsync(
                         session, SourceMapId, SourceX, SourceZ, CancellationToken.None))
        {
            visibility!.Commit();
        }
        var lease = await runtime.Map.AcquireMonsterViewerDeliveryLeaseAsync(
            session, MonsterObjectId, CancellationToken.None);
        Check.True(lease is not null, "terminal readiness fixture retains old-scene delivery");
        try
        {
            var send = session.SendAsync(
                PacketBuilder.PlayerManaUpdate(PlayerObjectId, character.CurrentMp),
                CancellationToken.None);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.FailActiveWrite();
            await AssertTerminalSendFailureAsync(send);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!session.IsDisconnected)
            {
                await Task.Delay(5, deadline.Token);
            }
            Check.True(registry.TryGetSessionWorldInstanceId(session, out _) &&
                !source.WorldReady && registry.GetMapSessions(SourceMapId).Count == 0 &&
                !registry.IsCurrentWorldSessionSnapshot(session, source) &&
                !registry.TryCapturePlayerMonsterTarget(session, SourceMapId,
                    MonsterObjectId, out _, out _),
                "terminal session immediately loses visibility and action authority while removal waits");
        }
        finally
        {
            await lease!.DisposeAsync();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        Check.True(!registry.TryGetSessionWorldInstanceId(session, out _),
            "disposal waits for the tracked terminal membership removal");
    }

    private static async Task WaitForTerminalQueueAsync(ClientSession session)
    {
        var egress = (BoundedReliableEgress)typeof(ClientSession)
            .GetField("_egress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (egress.Snapshot.CurrentItems == 0)
        {
            await Task.Delay(5, deadline.Token);
        }
    }

    private static async Task AssertTerminalSendFailureAsync(Task send)
    {
        try
        {
            await send.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Terminal fixture write unexpectedly succeeded.");
    }

    private sealed class TerminalFailureTransport : ILegacyByteTransport
    {
        private readonly TaskCompletionSource _write =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disconnected;
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisconnectCount => Volatile.Read(ref _disconnected);
        public string RemoteEndPoint => "terminal-delivery-fixture";
        public void FailActiveWrite() =>
            _write.TrySetException(new IOException("Controlled physical write failure."));
        public ValueTask<int> ReadAsync(Memory<byte> destination,
            CancellationToken cancellationToken) => ValueTask.FromResult(0);
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await _write.Task.WaitAsync(cancellationToken);
        }
        public void Disconnect() => Interlocked.Exchange(ref _disconnected, 1);
        public ValueTask DisposeAsync()
        {
            _write.TrySetCanceled();
            return ValueTask.CompletedTask;
        }
    }
}
