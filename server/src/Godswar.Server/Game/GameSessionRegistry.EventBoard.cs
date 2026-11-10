using Godswar.Server.Application.Events;
using Godswar.Server.Application.Realms;
using Godswar.Server.Infrastructure.Events;

namespace Godswar.Server.Game;

// EVENTSBOARD: reloads the admin's board rows and re-sends the whole board to everyone in the world when the
// realm day rolls over (the Today list changes) or the content changes (rates, editor windows, rows).
// Does nothing while event.board is 0. Never throws: a failure is logged and the next tick tries again.
internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan EventBoardTick = TimeSpan.FromSeconds(30);

    public async Task RunEventBoardAsync(RealmCalendar calendar, CancellationToken cancellationToken)
    {
        string? published = null;
        try
        {
            using var timer = new PeriodicTimer(EventBoardTick);
            do
            {
                try
                {
                    if (!EventBoardRuntime.Enabled)
                    {
                        published = null;
                        continue;
                    }

                    await EventBoardRuntime.ReloadAsync(cancellationToken);
                    var entries = EventBoardPublisher.BuildEntries(calendar, DateTimeOffset.UtcNow);
                    var fingerprint = EventBoardSchedule.Fingerprint(entries);
                    if (published is null)
                    {
                        // First pass after start-up or after switching on: sessions get the board at their next
                        // EnterUiReady, or now if they are already in the world.
                        published = fingerprint;
                        await PublishEventBoardOnceAsync(calendar, cancellationToken);
                        continue;
                    }

                    if (fingerprint != published)
                    {
                        published = fingerprint;
                        await PublishEventBoardOnceAsync(calendar, cancellationToken);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.WriteLine($"[event-board] tick failed: {error.GetType().Name}: {error.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<int> PublishEventBoardOnceAsync(RealmCalendar calendar, CancellationToken cancellationToken)
    {
        var sessions = SnapshotWorldReadySessions();
        if (sessions.Length == 0)
        {
            return 0;
        }

        var frames = EventBoardPublisher.BuildFrames(calendar, DateTimeOffset.UtcNow);
        var delivered = 0;
        foreach (var context in sessions)
        {
            try
            {
                foreach (var frame in frames)
                {
                    await context.Session.SendAsync(frame, cancellationToken, "EventBoard");
                }

                delivered++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine($"[event-board] delivery skipped: {error.GetType().Name}");
            }
        }

        Console.WriteLine($"[event-board] published entries={frames.Count} delivered={delivered}");
        return delivered;
    }
}
