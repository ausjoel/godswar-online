using System.Globalization;
using System.Text;
using Godswar.Server.Application.Gates; // FINALISE2
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.Events;

// EVENTSBOARD: the client's Event window ("Today" / notices / "This week" / growth guide, CSpecialMsgUI +
// XML/EventProc.lua). The server fills it with message 10204, one entry per frame. The lists are built here
// from the same rules that open the maps, so the board cannot drift from the gates:
//   Pindus / Ni Mini   BattlefieldSchedulePolicy (compiled windows, editor overrides, always-open, length, shift)
//   Lelantine Farm     always open (BattlefieldSchedulePolicy)
//   Wonderland         LegacyInstanceAdmissionPolicy (daily until event.wonderland_close_hour)
//   Spy invasion       SpyInvasionPolicy (Mon/Wed/Sat 19:00 UTC+8, 30 minutes), only when event.spy_invasion = 1
//   Zeus' Gift         ZeusGiftPolicy (daily, level 55+)
// plus the admin's own rows from public.event_board_entries (EventBoardRuntime). Descriptions are written here
// (generated), not retail text; times and levels come from the policies.

/// <summary>The four lists of the window. The value is the Type byte of the 10204 body.</summary>
internal enum EventBoardList : byte
{
    Today = 1,
    Notice = 2,
    Week = 3,
    Guide = 4
}

internal sealed record EventBoardEntry(
    EventBoardList List,
    string Time,
    string Name,
    string Content,
    string Link = "",
    string AutoName = "",
    string AutoAddress = "");

/// <summary>A window in realm time: day (0 = Sunday, as DayOfWeek), start minute of that day, length.</summary>
internal sealed record EventBoardWindow(int Day, int StartMinute, int DurationMinutes);

/// <summary>
/// One row of public.event_board_entries. Kind 'event' rows are listed under "This week" and, on the days of
/// their day mask, under "Today"; 'notice' and 'guide' rows go to those lists. A row whose <c>Replaces</c> names
/// a generated entry (Pindus, NiMini, Lelantine, TreasureLand = Wonderland, Spies, ZeusGift) hides it; if the row is enabled it
/// is listed in its place.
/// </summary>
internal sealed record EventBoardCustomRow(
    int Id,
    string Kind,
    int SortOrder,
    string Name,
    int DayMask,
    int? StartMinute,
    int? EndMinute,
    string TimeText,
    string Content,
    string Link,
    string AutoName,
    string AutoAddress,
    string? Replaces,
    bool Enabled);

internal sealed record EventBoardSettings(
    bool PindusAlwaysOpen,
    bool NiMiniAlwaysOpen,
    int BattlefieldMinutes,
    int BattlefieldShiftHours,
    int WonderlandCloseHour,
    bool SpiesEnabled,
    IReadOnlyDictionary<string, IReadOnlyList<EventWindow>> EditedWindows)
{
    public static EventBoardSettings FromCurrent() => new(
        ServerRates.Current.Absolute(ServerRateKeys.PindusAlwaysOpen) == 1,
        ServerRates.Current.Absolute(ServerRateKeys.NiMiniAlwaysOpen) == 1,
        (int)ServerRates.Current.Absolute(ServerRateKeys.BattlefieldWindowMinutes),
        (int)ServerRates.Current.Absolute(ServerRateKeys.BattlefieldShiftHours),
        (int)ServerRates.Current.Absolute(ServerRateKeys.WonderlandCloseHour),
        ServerRates.Current.Absolute(ServerRateKeys.SpyInvasion) == 1,
        NpcContentOverrides.Current.Events.Windows);
}

internal static class EventBoardSchedule
{
    public const string PindusKey = "Pindus";
    public const string NiMiniKey = "NiMini";
    public const string LelantineKey = "Lelantine";
    public const string TreasureLandKey = "TreasureLand";
    public const string SpiesKey = "Spies";
    public const string ZeusGiftKey = "ZeusGift";

    private const int MinutesPerDay = 1440;
    private const int MinutesPerWeek = 7 * MinutesPerDay;


    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>The Pindus or Ni Mini windows the gate uses, in realm time. Empty when always open.</summary>
    public static IReadOnlyList<EventBoardWindow> BattlefieldWindows(
        BattlefieldDestinationKind destination,
        EventBoardSettings settings)
    {
        var activity = destination == BattlefieldDestinationKind.Pindus ? EventContent.Pindus : EventContent.NiMini;
        // FINALISE2: the gate's custom windows (Gates table) when they apply.
        var gateKey = Godswar.Server.Application.WorldInstances.BattlefieldSchedulePolicy.GateKey(destination);
        if (GateRules.Mode(gateKey) == GateSchedule.Closed)
        {
            return []; // FINALISE2
        }

        if (GateRules.EditedWindows(gateKey) is { } gateWindows || settings.EditedWindows.TryGetValue(activity, out gateWindows))
        {
            var edited = gateWindows;
            return edited
                .Where(static w => w.Day != EventContent.ClosedDay)
                .Select(static w => new EventBoardWindow(w.Day, w.StartMinute, w.DurationMinutes))
                .OrderBy(static w => w.Day).ThenBy(static w => w.StartMinute)
                .ToArray();
        }

        var windows = new List<EventBoardWindow>();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            foreach (var start in CompiledStarts(destination, day))
            {
                // A shifted start may move to the previous or next day.
                var absolute = (int)day * MinutesPerDay + start + settings.BattlefieldShiftHours * 60;
                absolute = ((absolute % MinutesPerWeek) + MinutesPerWeek) % MinutesPerWeek;
                windows.Add(new EventBoardWindow(absolute / MinutesPerDay, absolute % MinutesPerDay, settings.BattlefieldMinutes));
            }
        }

        return windows.OrderBy(static w => w.Day).ThenBy(static w => w.StartMinute).ToArray();
    }

    /// <summary>
    /// The spy invasion windows in realm time. FINALISE2 (the admin F-04): the policy now runs on the realm clock itself, so
    /// these are its own windows (the gate event.spy_invasion); <paramref name="realmOffset"/> no longer shifts them.
    /// </summary>
    public static IReadOnlyList<EventBoardWindow> SpyWindows(TimeSpan realmOffset)
    {
        _ = realmOffset; // FINALISE2
        if (GateRules.Mode(GateKeys.Spies) == GateSchedule.Closed)
        {
            return [];
        }

        return Godswar.Server.Application.WorldInstances.SpyInvasionPolicy.Windows
            .Where(static w => w.Day != EventContent.ClosedDay)
            .Select(static w => new EventBoardWindow(w.Day, w.StartMinute, w.DurationMinutes))
            .OrderBy(static w => w.Day).ThenBy(static w => w.StartMinute)
            .ToArray();
    }

    // RELEASE integration fix: the board used a private copy of the STOCK starts, so with battlefield.pindus_igg_times
    // or battlefield.nimini_igg_times on (BATTLEFIELD/BATTLEFIELD2) it showed the wrong Pindus / Ni Mini hours. It now
    // reads the gate's own starts; the check still compares both through IsOpen.
    private static IEnumerable<int> CompiledStarts(BattlefieldDestinationKind destination, DayOfWeek day) =>
        Godswar.Server.Application.WorldInstances.BattlefieldSchedulePolicy.Starts(destination, day)
            .Select(static start => start.Hour * 60 + start.Minute);

    /// <summary>
    /// Every entry of the window for the realm day of <paramref name="realmNow"/>, in send order: notices, guide,
    /// this week, today. Each list is sent whole (its first frame clears it on the client).
    /// </summary>
    public static IReadOnlyList<EventBoardEntry> Build(
        DateTime realmNow,
        TimeSpan realmOffset,
        string timeZoneId,
        EventBoardSettings settings,
        IReadOnlyList<EventBoardCustomRow> customRows)
    {
        var today = (int)realmNow.DayOfWeek;
        var hidden = new HashSet<string>(
            customRows.Where(static r => !string.IsNullOrWhiteSpace(r.Replaces)).Select(static r => r.Replaces!.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var rows = customRows.Where(static r => r.Enabled).OrderBy(static r => r.SortOrder).ThenBy(static r => r.Id).ToArray();

        var notices = new List<EventBoardEntry>
        {
            new(EventBoardList.Notice, string.Empty, "Event times",
                TimeZoneNote(timeZoneId, realmOffset))
        };
        var guide = new List<EventBoardEntry>();
        var week = new List<(int Order, EventBoardEntry Entry)>();
        var todays = new List<(int Minute, EventBoardEntry Entry)>();

        void AddScheduled(string key, int order, string name, string content, IReadOnlyList<EventBoardWindow> windows, bool allDay, string allDayTime)
        {
            if (hidden.Contains(key))
            {
                return;
            }

            if (allDay)
            {
                week.Add((order, new EventBoardEntry(EventBoardList.Week, allDayTime, name, content)));
                todays.Add((-1, new EventBoardEntry(EventBoardList.Today, allDayTime, name, content)));
                return;
            }

            if (windows.Count == 0)
            {
                return;
            }

            var detail = content + "\nOpens (server time):\n" + string.Join("\n", windows.OrderBy(static w => (w.Day + 6) % 7).ThenBy(static w => w.StartMinute).Select(DescribeWindow)); // Monday first
            week.Add((order, new EventBoardEntry(EventBoardList.Week, WeekSummary(windows), name, detail)));
            foreach (var window in windows.Where(w => w.Day == today))
            {
                todays.Add((window.StartMinute, new EventBoardEntry(EventBoardList.Today, Range(window), name, detail)));
            }
        }

        // Generated descriptions; levels and places are the server's own gates.
        // FINALISE2: the levels are the gates' effective bands (editor rows included), not fixed text.
        var pindusBand = GateBands.Pindus();
        var niMiniBand = GateBands.Of(LevelBandGates.NiMiniUpper, BattlefieldTransporterProtocol.NiMiniMinimumLevel, BattlefieldTransporterProtocol.NiMiniMaximumLevel);
        var farmBand = GateBands.Of(LevelBandGates.LelantineFarm, BattlefieldTransporterProtocol.LelantineFarmMinimumLevel, BattlefieldTransporterProtocol.LelantineFarmMaximumLevel);
        var pindusAlways = settings.PindusAlwaysOpen || GateRules.Mode(LevelBandGates.Pindus) == GateSchedule.Always;
        var niMiniAlways = settings.NiMiniAlwaysOpen || GateRules.Mode(LevelBandGates.NiMiniUpper) == GateSchedule.Always;
        AddScheduled(PindusKey, 10, "Pindus Mountains",
            $"Battlefield, level {GateRules.Describe(pindusBand)}. Enter through the Battlefield Transporter in your capital while a window is open.", // FINALISE2
            pindusAlways ? [] : BattlefieldWindows(BattlefieldDestinationKind.Pindus, settings),
            pindusAlways, "Always open");
        AddScheduled(NiMiniKey, 20, "Ni Mini Valley",
            $"Battlefield, level {GateRules.Describe(niMiniBand)}. Enter through the Battlefield Transporter in your capital while a window is open.",
            niMiniAlways ? [] : BattlefieldWindows(BattlefieldDestinationKind.NiMiniLower, settings),
            niMiniAlways, "Always open");
        // FINALISE2 (the admin F-03): the farm opens only for its round (realm time), unless always-open.
        var farmAlways = ServerRates.Current.Absolute(ServerRateKeys.LelantineAlwaysOpen) == 1 ||
            GateRules.Mode(LevelBandGates.LelantineFarm) == GateSchedule.Always;
        AddScheduled(LelantineKey, 30, "Lelantine Farm",
            $"Battlefield, level {GateRules.Describe(farmBand)}. Enter through the Battlefield Transporter while the round is open; players still inside when it ends are sent to their capital.", // FINALISE2
            farmAlways || GateRules.Mode(LevelBandGates.LelantineFarm) == GateSchedule.Closed ? []
                : GateRules.RoundWindows(LevelBandGates.LelantineFarm).Where(static w => w.Day != EventContent.ClosedDay)
                    .Select(static w => new EventBoardWindow(w.Day, w.StartMinute, w.DurationMinutes)).ToArray(),
            allDay: farmAlways, "Always open");
        // REVIEW FIX: Wonderland (map 207) is entered only through the Instance Caller (Athens_060/Sparta_060,
        // InstanceCallerProtocol). It is NOT the retail "Treasure Land" event (igg-104: Event Transporter, Sundays).
        // The key stays "TreasureLand" so existing `replaces` rows keep working.
        AddScheduled(TreasureLandKey, 40, "Wonderland",
            $"Party instance, level {GateRules.Describe(GateBands.Of(LevelBandGates.Wonderland, 120, GateCatalog.LevelCap))}, 1-5 players, one entry a day. The party leader enters through the Instance Caller in your capital." + // FINALISE2
            (settings.WonderlandCloseHour >= 24 ? " Open all day." : $" Entry closes at {settings.WonderlandCloseHour:00}:00 server time."),
            [], allDay: true, settings.WonderlandCloseHour >= 24 ? "All day" : $"Until {settings.WonderlandCloseHour:00}:00");
        if (settings.SpiesEnabled)
        {
            AddScheduled(SpiesKey, 50, "Spy invasion",
                "Enemy spies invade both capitals in six waves, one every 5 minutes. Spies drop Talent Stones.",
                SpyWindows(realmOffset), allDay: false, string.Empty);
        }

        AddScheduled(ZeusGiftKey, 60, "Zeus' Gift",
            "Bring gifts to Zeus' Loyal Believer in your capital, level 55+. Up to 10 gifts a day, 12 at the weekend with double rewards.",
            [], allDay: true, "Daily");

        foreach (var row in rows)
        {
            var link = row.Link;
            switch (row.Kind)
            {
                case "notice":
                    notices.Add(new EventBoardEntry(EventBoardList.Notice, row.TimeText, row.Name, row.Content, link, row.AutoName, row.AutoAddress));
                    break;
                case "guide":
                    guide.Add(new EventBoardEntry(EventBoardList.Guide, row.TimeText, row.Name, row.Content, link, row.AutoName, row.AutoAddress));
                    break;
                default:
                    var time = row.TimeText.Length > 0 ? row.TimeText : CustomTime(row);
                    week.Add((1000 + row.SortOrder, new EventBoardEntry(EventBoardList.Week, time, row.Name, row.Content, link, row.AutoName, row.AutoAddress)));
                    if ((row.DayMask & (1 << today)) != 0)
                    {
                        var todayTime = row.StartMinute is { } s
                            ? Clock(s) + (row.EndMinute is { } e ? "-" + Clock(e) : string.Empty)
                            : row.TimeText.Length > 0 ? row.TimeText : "All day";
                        todays.Add((row.StartMinute ?? -1, new EventBoardEntry(EventBoardList.Today, todayTime, row.Name, row.Content, link, row.AutoName, row.AutoAddress)));
                    }

                    break;
            }
        }

        var result = new List<EventBoardEntry>();
        result.AddRange(notices);
        result.AddRange(guide);
        result.AddRange(week.OrderBy(static w => w.Order).Select(static w => w.Entry));
        result.AddRange(todays.OrderBy(static t => t.Minute).Select(static t => t.Entry));
        return result;
    }

    private static string TimeZoneNote(string timeZoneId, TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var text = $"All times on this board are server time ({timeZoneId}, UTC{sign}{offset.Duration():hh\\:mm}).";
        if (offset == TimeSpan.FromHours(8))
        {
            text += " Sydney and Melbourne: add 2 hours (3 hours during daylight saving).";
        }
        else if (timeZoneId == "Australia/Brisbane")
        {
            text += " Sydney and Melbourne: add 1 hour during daylight saving.";
        }

        return text;
    }

    private static string CustomTime(EventBoardCustomRow row)
    {
        var days = row.DayMask == 127 ? "Daily" : string.Join(" ", Enumerable.Range(0, 7).Select(i => (i + 1) % 7).Where(d => (row.DayMask & (1 << d)) != 0).Select(d => DayNames[d]));
        return row.StartMinute is { } start ? days + " " + Clock(start) : days;
    }

    private static string WeekSummary(IReadOnlyList<EventBoardWindow> windows)
    {
        // Monday-first day names; a single start time is appended when every window shares it.
        var days = windows.Select(static w => w.Day).Distinct().OrderBy(static d => (d + 6) % 7).Select(static d => DayNames[d]);
        var starts = windows.Select(static w => w.StartMinute).Distinct().ToArray();
        var text = string.Join(" ", days);
        return starts.Length == 1 ? text + " " + Clock(starts[0]) : text;
    }

    private static string DescribeWindow(EventBoardWindow window) =>
        DayNames[window.Day] + " " + Range(window);

    private static string Range(EventBoardWindow window) =>
        window.DurationMinutes >= MinutesPerDay
            ? Clock(window.StartMinute) + " (all day)"
            : Clock(window.StartMinute) + "-" + Clock((window.StartMinute + window.DurationMinutes) % MinutesPerDay);

    private static string Clock(int minute) =>
        (minute / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minute % 60).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Stable text of a board, to tell when it changed.</summary>
    public static string Fingerprint(IReadOnlyList<EventBoardEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(entry).Append('\n');
        }

        return builder.ToString();
    }
}

/// <summary>FINALISE2: effective gate bands for texts (the board).</summary>
internal static class GateBands
{
    public static GateBand Of(string key, int minimum, int maximum) =>
        GateRules.Band(key, minimum, Math.Min(maximum, GateCatalog.LevelCap));

    public static GateBand Pindus() =>
        Godswar.Server.Application.WorldInstances.BattlefieldRewardPolicy.PindusBandEnabled
            ? Of(LevelBandGates.Pindus, Godswar.Server.Application.WorldInstances.BattlefieldRewardPolicy.PindusBandMinimumLevel,
                Godswar.Server.Application.WorldInstances.BattlefieldRewardPolicy.PindusBandMaximumLevel)
            : Of(LevelBandGates.Pindus, BattlefieldTransporterProtocol.PindusCompiledMinimumLevel, BattlefieldTransporterProtocol.PindusCompiledMaximumLevel);
}
