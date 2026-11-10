using Godswar.Server.Application.Events;
using Godswar.Server.Application.Realms;
using Godswar.Server.Infrastructure.Events;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

// EVENTSBOARD: the Event window's lists (message 10204). Sent once the client says its UI is ready
// (EnterUiReady): the client only applies a 10204 while in the world (game state 4), and its Event button
// sends no request when the window opens, so there is nothing else to answer. The registry loop
// (GameSessionRegistry.EventBoard.cs) re-sends when the realm day or the content changes.
internal static class EventBoardPublisher
{
    public static IReadOnlyList<byte[]> BuildFrames(RealmCalendar calendar, DateTimeOffset instant)
    {
        var entries = BuildEntries(calendar, instant);
        return PacketBuilder.EventBoardFrames(entries);
    }

    public static IReadOnlyList<EventBoardEntry> BuildEntries(RealmCalendar calendar, DateTimeOffset instant) =>
        EventBoardSchedule.Build(
            calendar.ToRealmTime(instant).DateTime,
            calendar.GetUtcOffset(instant),
            calendar.TimeZoneId,
            EventBoardSettings.FromCurrent(),
            EventBoardRuntime.Rows);
}

internal sealed partial class GameClientHandler
{
    private async Task SendEventBoardAsync(CancellationToken cancellationToken)
    {
        if (!EventBoardRuntime.Enabled || _character is null)
        {
            return;
        }

        IReadOnlyList<byte[]> frames;
        try
        {
            frames = EventBoardPublisher.BuildFrames(_realmCalendar, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            Console.WriteLine($"[event-board] not sent: {error.GetType().Name}: {error.Message}");
            return;
        }

        foreach (var frame in frames)
        {
            await _session.SendAsync(frame, cancellationToken, "EventBoard");
        }

        Console.WriteLine($"[event-board] sent character={_character.Name} entries={frames.Count}");
    }
}
