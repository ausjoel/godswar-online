namespace Godswar.Server.Application.Events;

// Editor-written battlefield windows and scheduled notices. Empty = the compiled behaviour.
internal sealed record EventWindow(int Day, int StartMinute, int DurationMinutes);

internal sealed record ServerNotice(
    int Id,
    string Text,
    bool Banner,
    int DayMask,
    int StartMinute,
    int EndMinute,
    int RepeatMinutes,
    DateOnly? FirstDay,
    DateOnly? LastDay,
    bool Board = false, // the admin 2026-11-08: delivery "board" = the 1.4.3 board above the chat box
    bool Chat = false); // the admin 2026-11-10: delivery "chat" = a World line in the chat box (e.g. "event transporters now open")

internal sealed class EventContent
{
    public const string Pindus = "Pindus";
    public const string NiMini = "NiMini";
    // FINALISE: optional editor windows for the Lelantine Farm transporter (no rows = its Saturday round window, finalise-pets).
    public const string LelantineFarm = "LelantineFarm";
    public const int ClosedDay = -1;
    public const int DefaultPopulationMinutes = 5;

    public static EventContent Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<EventWindow>>(),
        [],
        DefaultPopulationMinutes);

    public EventContent(
        IReadOnlyDictionary<string, IReadOnlyList<EventWindow>> windows,
        IReadOnlyList<ServerNotice> notices,
        int populationMinutes,
        string? welcomeText = null)
    {
        Windows = windows;
        Notices = notices;
        PopulationMinutes = populationMinutes;
        WelcomeText = string.IsNullOrWhiteSpace(welcomeText) ? null : welcomeText;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<EventWindow>> Windows { get; }

    public IReadOnlyList<ServerNotice> Notices { get; }

    public int PopulationMinutes { get; }

    /// <summary>
    /// the admin 2026-11-10: the editor's server welcome (table server_messages, name 'welcome'); null = off (the default).
    /// Shown once per login on the board (top box). The client's own "Welcome to Godswar Origin" banner in the chat
    /// box is client text (Localization/en_us/Text/Message.dat ChatNotice0-8), not this.
    /// </summary>
    public string? WelcomeText { get; }
}

internal static class EventSchedulePolicy
{
    /// <summary>A window may run past midnight, so yesterday's windows count too. Start-inclusive, end-exclusive.</summary>
    public static bool IsOpen(IEnumerable<EventWindow> windows, DateTime realmNow)
    {
        foreach (var window in windows)
        {
            if (window.Day == EventContent.ClosedDay)
            {
                continue;
            }

            for (var offset = -1; offset <= 0; offset++)
            {
                var day = realmNow.Date.AddDays(offset);
                if ((int)day.DayOfWeek != window.Day)
                {
                    continue;
                }

                var opens = day.AddMinutes(window.StartMinute);
                if (realmNow >= opens && realmNow < opens.AddMinutes(window.DurationMinutes))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal static class ServerNoticePolicy
{
    // After a long stall or a restart nothing older than this is sent late.
    private static readonly TimeSpan MaximumCatchUp = TimeSpan.FromMinutes(2);

    /// <summary>True if a send minute of this notice falls in (previous, now]. Both are realm times.</summary>
    public static bool IsDue(ServerNotice notice, DateTime previous, DateTime now)
    {
        if (now <= previous)
        {
            return false;
        }

        if (now - previous > MaximumCatchUp)
        {
            previous = now - MaximumCatchUp;
        }

        var minute = new DateTime(previous.Year, previous.Month, previous.Day, previous.Hour, previous.Minute, 0, previous.Kind)
            .AddMinutes(1);
        for (; minute <= now; minute = minute.AddMinutes(1))
        {
            if (SendsAt(notice, minute))
            {
                return true;
            }
        }

        return false;
    }

    public static bool SendsAt(ServerNotice notice, DateTime realmMinute)
    {
        var day = DateOnly.FromDateTime(realmMinute);
        if (notice.FirstDay is { } first && day < first ||
            notice.LastDay is { } last && day > last ||
            (notice.DayMask & (1 << (int)realmMinute.DayOfWeek)) == 0)
        {
            return false;
        }

        var minute = realmMinute.Hour * 60 + realmMinute.Minute;
        if (notice.RepeatMinutes == 0)
        {
            return minute == notice.StartMinute;
        }

        return minute >= notice.StartMinute &&
            minute <= notice.EndMinute &&
            (minute - notice.StartMinute) % notice.RepeatMinutes == 0;
    }
}
