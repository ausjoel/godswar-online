namespace Godswar.Server.ProtocolChecks;

internal sealed partial class PetDurableCaptureTransport
{
    private TaskCompletionSource _nextLegacyWrite = NewLegacyWriteSignal();

    // Exact status batches complete at queue admission. Wait for the expected
    // physical frames before inspecting their existing count/order contract.
    public async Task<IReadOnlyList<byte[]>> ReadLegacyPacketsAsync(
        int minimumPacketCount)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            Task nextWrite;
            lock (_gate)
            {
                var packets = ReadLegacyPackets();
                if (packets.Count >= minimumPacketCount)
                {
                    return packets;
                }
                nextWrite = _nextLegacyWrite.Task;
            }
            await nextWrite.WaitAsync(deadline.Token);
        }
    }

    private void SignalLegacyWriteLocked()
    {
        var completed = _nextLegacyWrite;
        _nextLegacyWrite = NewLegacyWriteSignal();
        completed.TrySetResult();
    }

    private static TaskCompletionSource NewLegacyWriteSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
