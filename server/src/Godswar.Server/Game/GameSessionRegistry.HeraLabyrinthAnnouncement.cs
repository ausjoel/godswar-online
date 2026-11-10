using Godswar.Server.Application.Realms;
using Godswar.Server.Packets;
using H = Godswar.Server.Domain.World.Content.HeraLabyrinthProtocol;

namespace Godswar.Server.Game;

// HERA'S LABYRINTH part 2: the client's daily "One minute to Hera's Labyrinth" line (Text/SysMsg/EveryDay.Dat entry 0)
// as a centred notice to everyone in the world at GODSWAR_HERA_LABYRINTH_ANNOUNCE (realm-local, default 19:59,
// GENERATED). Silent while GODSWAR_HERA_LABYRINTH is off or the window is "never". No rounds: the maze stays open all
// day (IGG "All day"; the Event Transporter's own table says "Anytime ... Everyday"). Never throws.
internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan HeraAnnouncementTick = TimeSpan.FromSeconds(20);

    public async Task RunHeraLabyrinthAnnouncementAsync(RealmCalendar calendar, CancellationToken cancellationToken)
    {
        DateTime? previous = null;
        try
        {
            using var timer = new PeriodicTimer(HeraAnnouncementTick);
            do
            {
                try
                {
                    var now = calendar.ToRealmTime(DateTimeOffset.UtcNow).DateTime;
                    var times = H.AnnouncementTimes();
                    if (times.Count == 0)
                    {
                        previous = null; // switching on later starts fresh: no catch-up announcement
                        continue;
                    }

                    if (H.AnnouncementDue(times, previous, now))
                    {
                        await PublishHeraLabyrinthAnnouncementAsync(cancellationToken);
                    }

                    previous = now;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.WriteLine($"[hera] announcement tick failed: {error.GetType().Name}: {error.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<int> PublishHeraLabyrinthAnnouncementAsync(CancellationToken cancellationToken)
    {
        var packet = PacketBuilder.CenteredAnnouncement(H.AnnouncementText);
        var delivered = 0;
        foreach (var context in SnapshotWorldReadySessions())
        {
            try
            {
                await context.Session.SendAsync(packet, cancellationToken, "HeraLabyrinthAnnouncement");
                delivered++;
                // the admin 2026-11-10: event openings also stay in the chat box (board.event_chat_lines).
                if (Godswar.Server.Application.Finalise2.Finalise2Rates.On(
                        Godswar.Server.Application.Finalise2.Finalise2RateKeys.EventChatLines))
                    await SendChatLineAsync(context, H.AnnouncementText, "HeraLabyrinthChat", cancellationToken);
            }
            catch (Exception error) when (error is ObjectDisposedException or IOException or InvalidOperationException)
            {
                // A closing session must not stop the notice for the rest.
            }
        }

        Console.WriteLine($"[hera] announcement delivered={delivered}");
        return delivered;
    }
}
