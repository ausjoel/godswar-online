using Godswar.Server.Application.Events;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Realms;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

// W4F (wave 4 item 4): the client ships event-opening lines in Text/SysMsg/WeekDay.Dat that only a server can trigger.
// This loop watches the same "is it open" answer the gates use (GateSchedules.IsOpen) and, on each closed -> open edge,
// sends the matching line as a centre notice (plus a World chat line when board.event_chat_lines = 1). Off unless
// board.event_open_notices = 1. The first tick only records state, so a restart never repeats an opening. Never throws.
internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan EventOpeningTick = TimeSpan.FromSeconds(20);

    public async Task RunEventOpeningNoticesAsync(RealmCalendar calendar, CancellationToken cancellationToken)
    {
        Dictionary<string, bool>? previous = null;
        try
        {
            using var timer = new PeriodicTimer(EventOpeningTick);
            do
            {
                try
                {
                    if (!Finalise2Rates.On(Finalise2RateKeys.EventOpenNotices))
                    {
                        previous = null; // switching on later starts fresh: no catch-up notice
                        continue;
                    }

                    var current = EventOpeningNoticePolicy.Snapshot(calendar, DateTimeOffset.UtcNow);
                    foreach (var notice in EventOpeningNoticePolicy.Due(previous, current))
                    {
                        await PublishEventOpeningNoticeAsync(notice, cancellationToken);
                    }

                    previous = current;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.WriteLine($"[event-open] tick failed: {error.GetType().Name}: {error.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<int> PublishEventOpeningNoticeAsync(EventOpeningNotice notice, CancellationToken cancellationToken)
    {
        var delivered = 0;
        var chat = Finalise2Rates.On(Finalise2RateKeys.EventChatLines);
        foreach (var context in SnapshotWorldReadySessions())
        {
            var text = notice.TextFor(context.Character.Camp);
            if (text is null)
            {
                continue;
            }

            try
            {
                await context.Session.SendAsync(PacketBuilder.CenteredAnnouncement(text), cancellationToken, "EventOpeningNotice");
                delivered++;
                if (chat)
                {
                    await SendChatLineAsync(context, text, "EventOpeningChat", cancellationToken);
                }
            }
            catch (Exception error) when (error is ObjectDisposedException or IOException or InvalidOperationException
                                              or ArgumentException)
            {
                // A closing session must not stop the notice for the rest.
            }
        }

        Console.WriteLine($"[event-open] {notice.Key} delivered={delivered}");
        return delivered;
    }
}
