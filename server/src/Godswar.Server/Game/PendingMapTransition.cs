namespace Godswar.Server.Game;

internal sealed class PendingMapTransition(
    byte sourceMapId,
    byte targetMapId,
    float targetX,
    float targetZ)
{
    private const int AwaitingReadiness = 0;
    private const int Completing = 1;
    private const int Completed = 2;
    private const int TimedOut = 3;

    private readonly object _timeoutGate = new();
    private readonly CancellationTokenSource _timeoutCancellation =
        new();
    private int _state = AwaitingReadiness;

    public byte SourceMapId { get; } = sourceMapId;

    public byte TargetMapId { get; } = targetMapId;

    public float TargetX { get; } = targetX;

    public float TargetZ { get; } = targetZ;

    public bool ClientReadyReceived { get; set; }

    public bool PlayerDetailSent { get; set; }

    public CancellationToken TimeoutCancellation =>
        _timeoutCancellation.Token;

    public bool TryStartCompletion()
    {
        if (Interlocked.CompareExchange(
                ref _state,
                Completing,
                AwaitingReadiness) != AwaitingReadiness)
        {
            return false;
        }

        lock (_timeoutGate)
        {
            _timeoutCancellation.Cancel();
        }
        return true;
    }

    public bool TryMarkTimedOut() =>
        Interlocked.CompareExchange(
            ref _state,
            TimedOut,
            AwaitingReadiness) == AwaitingReadiness;

    public void MarkCompleted()
    {
        if (Interlocked.CompareExchange(
                ref _state,
                Completed,
                Completing) != Completing)
        {
            throw new InvalidOperationException(
                "Map transition completion state changed unexpectedly.");
        }
    }

    public void DisposeTimeoutCancellation()
    {
        lock (_timeoutGate)
        {
            _timeoutCancellation.Dispose();
        }
    }
}
