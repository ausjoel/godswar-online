namespace Godswar.Server.Networking;

internal sealed partial class ClientSession
{
    private Action<ClientSession>? _egressTerminalObserver;
    private readonly object _terminalCleanupGate = new();
    private Task? _terminalCleanup;

    internal Task TerminalCleanupCompletion
    {
        get
        {
            lock (_terminalCleanupGate)
            {
                return _terminalCleanup ?? Task.CompletedTask;
            }
        }
    }

    internal void ScheduleTerminalCleanup(
        Action<ClientSession>? fallback = null)
    {
        lock (_terminalCleanupGate)
        {
            if (_terminalCleanup is not null || !IsDisconnected)
            {
                return;
            }
            var observer = Volatile.Read(ref _egressTerminalObserver) ?? fallback;
            if (observer is null)
            {
                return;
            }

            // A failed publication can retain its own viewer lease. Revoke
            // readiness synchronously, then let that caller unwind before
            // physical membership removal. Disposal observes this one task.
            _terminalCleanup = Task.Run(() =>
            {
                try
                {
                    observer(this);
                }
                catch
                {
                    // The connection owner retains idempotent cleanup.
                }
            });
        }
    }

    internal void RegisterEgressTerminalObserver(
        Action<ClientSession> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var existing = Interlocked.CompareExchange(
            ref _egressTerminalObserver,
            observer,
            comparand: null);
        if (existing is not null && existing != observer)
        {
            throw new InvalidOperationException(
                "A session can have only one egress terminal owner.");
        }
    }

    /// <summary>
    /// Synchronous, non-waiting reliable admission for packets whose routing
    /// authority is held by the caller. Completion may be observed after
    /// releasing that authority fence.
    /// </summary>
    internal bool TryAdmitExact(
        ReadOnlyMemory<byte> clearPacket,
        out Task completion) =>
        WasExactEgressOwned(
            TryAdmitExactOutcome(clearPacket, out completion));

    /// <summary>Packets the reliable egress queue can hold at once; an exact batch larger than this is always rejected.</summary>
    internal int ReliableEgressQueueItems => _options.ReliableEgressQueueItems;

    internal bool TryAdmitExactBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> clearPackets,
        out Task completion) =>
        WasExactEgressOwned(
            TryAdmitExactBatchOutcome(clearPackets, out completion));

    private static bool WasExactEgressOwned(
        ExactEgressAdmissionOutcome outcome) => outcome is
        ExactEgressAdmissionOutcome.Admitted or
        ExactEgressAdmissionOutcome.AdmittedTerminal;

    internal ExactEgressAdmissionOutcome TryAdmitExactOutcome(
        ReadOnlyMemory<byte> clearPacket,
        out Task completion) =>
        _egress.TryWriteBatch([clearPacket], out completion);

    internal ExactEgressAdmissionOutcome TryAdmitExactBatchOutcome(
        IReadOnlyList<ReadOnlyMemory<byte>> clearPackets,
        out Task completion) =>
        _egress.TryWriteBatch(clearPackets, out completion);

#if DEBUG
    internal void ProtocolCheckFailNextExactBatchAfterCommit() =>
        _egress.ProtocolCheckFailNextExactBatchAfterCommit();
#endif

    private void TerminalizeFromEgress(Exception error)
    {
        TerminalErrorType ??= error.GetType().Name;
        _ = Interlocked.CompareExchange(ref _disconnected, 1, 0);
        try
        {
            CancelLifetime();
        }
        catch
        {
        }
        try
        {
            DisconnectTransport();
        }
        catch
        {
        }
        try
        {
            ScheduleTerminalCleanup();
        }
        catch
        {
            // Logical and transport teardown are already complete. The
            // owner also observes IsDisconnected on every routing path.
        }
    }
}
