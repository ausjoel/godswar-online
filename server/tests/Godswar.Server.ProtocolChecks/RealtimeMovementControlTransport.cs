using System.Collections.Concurrent;
using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// In-memory secure control channel used only by handler integration checks.
/// It exposes authenticated movement ingress and captures snapshot/reliable
/// egress without opening a socket or starting the production UDP runtime.
/// </summary>
internal sealed class RealtimeMovementControlTransport :
    ILegacyByteTransport,
    ISecureControlChannel
{
    private readonly object _gate = new();
    private readonly MemoryStream _legacyWrites = new();
    private readonly PacketCipher _legacyDecodeCipher = new();
    private readonly ConcurrentQueue<SecureRealtimeMovementIngress>
        _movementIngress = new();
    private readonly List<SecureRealtimePositionSnapshot> _snapshots =
        [];
    private WritePause? _nextWritePause;
    private int _active;
    private int _disconnected;
    private int _disposed;
    private int _failNextWrite;

    public RealtimeMovementControlTransport()
    {
        var connectionId = Enumerable.Repeat(
            (byte)0x31,
            SecureProtocolConstants.ConnectionIdBytes).ToArray();
        var clientInstanceId = Enumerable.Repeat(
            (byte)0x42,
            SecureProtocolConstants.ClientInstanceIdBytes).ToArray();
        var originHash = Enumerable.Repeat(
            (byte)0x53,
            SecureProtocolConstants.BuildHashBytes).ToArray();
        try
        {
            ConnectionContext = new SecureConnectionContext(
                SecureEndpointRole.Game,
                SecureProtocolConstants.ProtocolMajor,
                SecureProtocolConstants.ProtocolMinor,
                connectionId,
                clientInstanceId,
                originHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(connectionId);
            CryptographicOperations.ZeroMemory(clientInstanceId);
            CryptographicOperations.ZeroMemory(originHash);
        }
    }

    public string RemoteEndPoint => "secure-realtime-test";

    public SecureConnectionContext ConnectionContext { get; }

    public SecureBoundGamePrincipal? BoundGamePrincipal => null;

    public bool SupportsRealtimeMovement => true;

    public bool IsRealtimeMovementActive =>
        Volatile.Read(ref _active) != 0;

    public int DisconnectCount =>
        Volatile.Read(ref _disconnected);

    public IReadOnlyList<SecureRealtimePositionSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return _snapshots.ToArray();
            }
        }
    }

    public byte[] TakeClearLegacyWrites()
    {
        byte[] encrypted;
        lock (_gate)
        {
            encrypted = _legacyWrites.ToArray();
            _legacyWrites.SetLength(0);
        }

        new PacketCipher().Transform(encrypted);
        return encrypted;
    }

    public WritePause PauseNextWrite()
    {
        var pause = new WritePause();
        if (Interlocked.CompareExchange(ref _nextWritePause, pause, null) is not null)
        {
            throw new InvalidOperationException(
                "A transport write is already paused.");
        }

        return pause;
    }

    internal sealed class WritePause
    {
        internal readonly TaskCompletionSource Started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilStartedAsync() =>
            Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => Released.TrySetResult();
    }

    public void EnqueueMovement(
        in SecureRealtimeMovementIngress ingress)
    {
        _movementIngress.Enqueue(ingress);
        Volatile.Write(ref _active, 1);
    }

    public void ActivateRealtimeMovement() =>
        Volatile.Write(ref _active, 1);

    public void FailNextWrite() =>
        Volatile.Write(ref _failNextWrite, 1);

    public bool TryTakeRealtimeMovement(
        out SecureRealtimeMovementIngress ingress) =>
        _movementIngress.TryDequeue(out ingress);

    public bool TryPublishRealtimeSnapshot(
        in SecureRealtimePositionSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshots.Add(snapshot);
        }
        return true;
    }

    public ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(0);
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _failNextWrite, 0) != 0)
        {
            throw new IOException("Simulated reliable egress failure.");
        }
        if (Interlocked.Exchange(ref _nextWritePause, null) is { } pause)
        {
            pause.Started.TrySetResult();
            await pause.Released.Task.WaitAsync(cancellationToken);
        }
        lock (_gate)
        {
            _legacyWrites.Write(source.Span);
        }
    }

    public void MarkAuthenticated()
    {
    }

    public ValueTask SendGameGrantAsync(
        SecureGameGrant grant,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(
            new InvalidOperationException(
                "A game handler cannot issue login grants."));

    public void Disconnect() =>
        Interlocked.Exchange(ref _disconnected, 1);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Disconnect();
            lock (_gate)
            {
                _legacyWrites.Dispose();
                _snapshots.Clear();
            }
        }
        return ValueTask.CompletedTask;
    }
}
