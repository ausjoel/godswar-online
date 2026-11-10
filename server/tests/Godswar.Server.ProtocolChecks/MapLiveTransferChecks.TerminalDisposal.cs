using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MapLiveTransferChecks
{
    private static async Task CheckDisposalOwnsUnscheduledTerminalCleanupAsync()
    {
        var transport = new TerminalFailureTransport();
        var session = new ClientSession(transport);
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim(false);
        var calls = 0;
        session.RegisterEgressTerminalObserver(_ =>
        {
            Interlocked.Increment(ref calls);
            cleanupEntered.TrySetResult();
            releaseCleanup.Wait();
        });
        Check.True(session.TryClaimDisconnect(), "disposal fixture claims terminal epoch");
        session.CompleteClaimedDisconnect(new IOException("Controlled exact disconnect."));

        var disposal = session.DisposeAsync().AsTask();
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            session.ScheduleTerminalCleanup();
            Check.True(
                !disposal.IsCompleted && !session.TerminalCleanupCompletion.IsCompleted,
                "disposal owns cleanup even when exact disconnect has not scheduled it yet");
        }
        finally
        {
            releaseCleanup.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Check.Equal(1, calls, "disposal and late exact cleanup scheduling share one detach");
    }
}
