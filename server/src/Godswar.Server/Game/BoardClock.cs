using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Realms;

namespace Godswar.Server.Game;

/// <summary>
/// B21 (item 19, the admin 2026-11-21: "the notice board above the chat box should show the time of each server message").
/// Board.lua cannot do it: the server wraps one message over up to 3 'M' lines (BoardText.Wrap), the client cannot tell
/// a new message from a continuation, and a client-side stamp would push lines past the 46-character cut. So the server
/// puts "HH:MM " (realm time, the realm calendar's time zone) in front of the message before it is wrapped.
/// board.message_time (default 1) switches it off again. Only 'M' lines get a time ('O' replaces line 1 and has none).
/// </summary>
internal static class BoardClock
{
    /// <summary>The realm calendar, set when the notice ticker starts. Null (tests, no ticker): no stamp.</summary>
    public static RealmCalendar? Calendar { get; set; }

    public static string Stamp(string text, DateTimeOffset now) =>
        Stamp(text, now, Calendar, Finalise2Rates.On(Finalise2RateKeys.BoardMessageTime));

    public static string Stamp(string text, DateTimeOffset now, RealmCalendar? calendar, bool on)
    {
        if (!on || calendar is null || string.IsNullOrWhiteSpace(text)) return text;
        return calendar.ToRealmTime(now).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " " + text;
    }
}
