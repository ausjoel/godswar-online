using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Events;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Events;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// EVENTSBOARD: message 10204 (Event window). Layout from Origin.exe 0x694870 / 0x617b50 / 0x4e3532;
// the board's times must be exactly the windows the gates open.
internal static class EventBoardChecks
{
    public const string CheckName = "Event board 10204";

    private static readonly RealmCalendar Manila = RealmCalendar.CreateForTesting(RealmId.Tempest, "Asia/Manila");
    private static readonly RealmCalendar Utc = RealmCalendar.CreateForTesting(RealmId.Tempest, "Etc/UTC");

    public static Task RunAsync()
    {
        try
        {
            CheckFrameLayout();
            CheckFlagsSequence();
            CheckGateDefaultsOff();
            CheckBattlefieldWindowsMatchGate();
            CheckSpyWindowsMatchPolicy();
            CheckBuiltBoard();
            CheckCustomRows();
        }
        finally
        {
            ServerRates.ResetToDefaults();
            NpcContentOverrides.Install(NpcContentSnapshot.Empty);
            EventBoardRuntime.InstallForTesting([]);
        }

        return Task.CompletedTask;
    }

    private static void Rates(params (string Key, decimal Value)[] values) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            values.Select(v => KeyValuePair.Create(v.Key, v.Value)), 1, DateTimeOffset.UtcNow, "check", "test"));

    private static string Field(byte[] frame, int offset, int length)
    {
        var span = frame.AsSpan(4 + offset, length);
        var end = span.IndexOf((byte)0);
        Check.True(end >= 0, $"field at {offset} is NUL-terminated");
        return Encoding.ASCII.GetString(span[..end]);
    }

    private static void CheckFrameLayout()
    {
        var entry = new EventBoardEntry(EventBoardList.Week, "Wed Fri 21:00", "Pindus Mountains", "Line 1\nLine 2",
            "https://example.invalid/", "Transporter", "0,10,20,1,30,40");
        var frame = PacketBuilder.EventBoardEntryFrame(PacketBuilder.EventBoardFlagFirst, entry);
        Check.Equal(1894, frame.Length, "frame length = 4 + 0x762");
        Check.Equal((ushort)1894, BinaryPrimitives.ReadUInt16LittleEndian(frame), "size field");
        Check.Equal((ushort)10204, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2)), "opcode 0x27dc");
        Check.Equal((byte)1, frame[4], "Flags at body +0");
        Check.Equal((byte)3, frame[5], "Type at body +1 (3 = this week)");
        Check.Equal("Wed Fri 21:00", Field(frame, 2, 32), "Time at +2");
        Check.Equal("Pindus Mountains", Field(frame, 34, 64), "Name at +34");
        Check.Equal("https://example.invalid/", Field(frame, 98, 128), "Link at +98");
        Check.Equal("Line 1\nLine 2", Field(frame, 226, 1152), "Content at +226");
        Check.Equal("Transporter", Field(frame, 1378, 64), "AutoName1 at +1378");
        Check.Equal("0,10,20,1,30,40", Field(frame, 1442, 32), "AutoAddr1 at +1442");
        Check.True(frame.AsSpan(4 + 1474).IndexOfAnyExcept((byte)0) < 0, "AutoName2..DayTime3 (+1474..+1889) stay empty, incl. DayTimeID +1762");

        var longEntry = new EventBoardEntry(EventBoardList.Today, new string('T', 40), new string('N', 100), new string('C', 2000) + "é");
        var cut = PacketBuilder.EventBoardEntryFrame(PacketBuilder.EventBoardFlagOnly, longEntry);
        Check.Equal(31, Field(cut, 2, 32).Length, "Time cut to 31 + NUL");
        Check.Equal(63, Field(cut, 34, 64).Length, "Name cut to 63 + NUL");
        Check.Equal(1151, Field(cut, 226, 1152).Length, "Content cut to 1151 + NUL");
        var ascii = PacketBuilder.EventBoardEntryFrame(3, new EventBoardEntry(EventBoardList.Notice, "", "Café", ""));
        Check.Equal("Caf?", Field(ascii, 34, 64), "non-ASCII replaced");
        Check.Throws<ArgumentException>(() => PacketBuilder.EventBoardEntryFrame(3, new EventBoardEntry(EventBoardList.Today, "", " ", "")), "an empty name is refused (the Lua drops it)");
        Check.Throws<ArgumentOutOfRangeException>(() => PacketBuilder.EventBoardEntryFrame(4, entry), "flags above 3 refused");
    }

    private static void CheckFlagsSequence()
    {
        EventBoardEntry E(EventBoardList list, string name) => new(list, "", name, "");
        var frames = PacketBuilder.EventBoardFrames(
        [
            E(EventBoardList.Notice, "n1"),
            E(EventBoardList.Week, "w1"), E(EventBoardList.Week, "w2"), E(EventBoardList.Week, "w3"),
            E(EventBoardList.Today, "t1"), E(EventBoardList.Today, "t2")
        ]);
        var seen = frames.Select(f => $"{f[5]}:{f[4]}").ToArray();
        Check.Equal("2:3 3:1 3:0 3:2 1:1 1:2", string.Join(" ", seen), "type:flags per frame (only=3, first=1, middle=0, last=2)");
    }

    private static void CheckGateDefaultsOff()
    {
        ServerRates.ResetToDefaults();
        Check.True(!EventBoardRuntime.Enabled, "event.board defaults to 0 (nothing sent)");
        Rates((ServerRateKeys.EventBoard, 1m));
        Check.True(EventBoardRuntime.Enabled, "event.board = 1 switches it on");
        ServerRates.ResetToDefaults();
    }

    // Every 5 minutes of a week: the board's windows and the transporter gate agree.
    private static void AssertBattlefieldAgrees(string label)
    {
        var settings = EventBoardSettings.FromCurrent();
        foreach (var destination in new[] { BattlefieldDestinationKind.Pindus, BattlefieldDestinationKind.NiMiniLower })
        {
            var windows = EventBoardSchedule.BattlefieldWindows(destination, settings);
            var start = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(8));
            for (var minute = 0; minute < 7 * 1440; minute += 5)
            {
                var instant = start.AddMinutes(minute);
                var realm = Manila.ToRealmTime(instant).DateTime;
                var board = EventSchedulePolicy.IsOpen(windows.Select(w => new EventWindow(w.Day, w.StartMinute, w.DurationMinutes)), realm);
                var gate = BattlefieldSchedulePolicy.IsOpen(destination, Manila, instant);
                if (board != gate)
                {
                    Check.True(false, $"{label}: {destination} at {realm:ddd HH:mm} board={board} gate={gate}");
                }
            }
        }
    }

    private static void CheckBattlefieldWindowsMatchGate()
    {
        ServerRates.ResetToDefaults();
        // RELEASE: battlefield.pindus_igg_times defaults to 1 (BATTLEFIELD2): the board must follow it.
        AssertBattlefieldAgrees("release defaults (Pindus IGG times)");
        Check.Equal(3, EventBoardSchedule.BattlefieldWindows(BattlefieldDestinationKind.Pindus, EventBoardSettings.FromCurrent()).Count,
            "three IGG Pindus windows (Wed 11:00, Sat 21:00, Sun 11:00)");
        Rates((ServerRateKeys.BattlefieldNiMiniIggTimes, 1m));
        AssertBattlefieldAgrees("Ni Mini IGG times on");
        Rates((ServerRateKeys.BattlefieldPindusIggTimes, 0m));
        AssertBattlefieldAgrees("stock");
        var pindus = EventBoardSchedule.BattlefieldWindows(BattlefieldDestinationKind.Pindus, EventBoardSettings.FromCurrent());
        Check.Equal(6, pindus.Count, "six stock Pindus windows");
        Rates((ServerRateKeys.BattlefieldShiftHours, -2m), (ServerRateKeys.BattlefieldWindowMinutes, 60m));
        AssertBattlefieldAgrees("shift -2h, 60 min");
        Rates((ServerRateKeys.BattlefieldShiftHours, 6m));
        AssertBattlefieldAgrees("shift +6h (crosses midnight)");
        ServerRates.ResetToDefaults();
        NpcContentOverrides.Install(new NpcContentSnapshot(1, new Dictionary<string, IReadOnlyList<NpcShopOverrideRow>>(), [], events: new EventContent(
            new Dictionary<string, IReadOnlyList<EventWindow>> { [EventContent.Pindus] = [new EventWindow(2, 20 * 60, 90), new EventWindow(EventContent.ClosedDay, 0, 1)] },
            [], EventContent.DefaultPopulationMinutes)));
        AssertBattlefieldAgrees("editor override");
        Check.Equal(1, EventBoardSchedule.BattlefieldWindows(BattlefieldDestinationKind.Pindus, EventBoardSettings.FromCurrent()).Count, "editor rows replace the compiled Pindus windows");
        NpcContentOverrides.Install(NpcContentSnapshot.Empty);
    }

    private static void CheckSpyWindowsMatchPolicy()
    {
        // FINALISE2 (the admin F-04): the spy policy runs on the realm clock, so the board and the policy share a calendar;
        // Australia/Brisbane (live) is checked too.
        foreach (var calendar in new[] { Manila, Utc, RealmCalendar.CreateForTesting(RealmId.Tempest, "Australia/Brisbane") })
        {
            Godswar.Server.Application.Gates.RealmClock.Calendar = calendar;
            var start = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
            for (var minute = 0; minute < 7 * 1440; minute += 5)
            {
                var instant = start.AddMinutes(minute);
                var windows = EventBoardSchedule.SpyWindows(calendar.GetUtcOffset(instant));
                var board = EventSchedulePolicy.IsOpen(windows.Select(w => new EventWindow(w.Day, w.StartMinute, w.DurationMinutes)), calendar.ToRealmTime(instant).DateTime);
                if (board != SpyInvasionPolicy.IsActive(instant))
                {
                    Godswar.Server.Application.Gates.RealmClock.Calendar = null;
                    Check.True(false, $"spies {calendar.TimeZoneId} at {instant:u}: board={board}");
                }
            }
        }

        Godswar.Server.Application.Gates.RealmClock.Calendar = null;
    }

    private static IReadOnlyList<EventBoardEntry> BuildAt(DateTimeOffset instant) =>
        EventBoardPublisher.BuildEntries(Manila, instant);

    private static void CheckBuiltBoard()
    {
        // Monday 2026-11-02 10:00 Manila.
        var monday = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8));
        Rates((ServerRateKeys.SpyInvasion, 1m));
        var board = BuildAt(monday);
        var today = board.Where(e => e.List == EventBoardList.Today).ToArray();
        var week = board.Where(e => e.List == EventBoardList.Week).ToArray();
        Check.True(board.Any(e => e.List == EventBoardList.Notice && e.Content.Contains("Asia/Manila")), "time-zone notice");
        Check.True(today.Any(e => e.Name == "Spy invasion" && e.Time == "19:00-19:30"), "Monday: spies 19:00-19:30 today");
        Check.True(!today.Any(e => e.Name == "Pindus Mountains"), "Monday: no Pindus today");
        Check.True(week.Any(e => e.Name == "Pindus Mountains" && e.Time == "Wed Sat Sun"), "week: Pindus days (RELEASE: IGG times on by default)");
        Check.True(week.Any(e => e.Name == "Ni Mini Valley" && e.Content.Contains("Fri 19:00-19:45") && e.Content.Contains("Sat 07:00-07:45")), "week: Ni Mini windows in content");
        Check.True(week.Any(e => e.Name == "Spy invasion" && e.Time == "Mon Wed Sat 19:00"), "week: spies");
        // FINALISE2 (the admin F-03): the farm is listed with its round window, not "always open".
        Check.True(week.Any(e => e.Name == "Lelantine Farm" && e.Time == "Sat 22:45" && e.Content.Contains("level 31-200")), "week: Lelantine Sat 22:45, level 31-200");
        Check.True(week.Any(e => e.Name == "Ni Mini Valley" && e.Content.Contains("level 50-89")), "week: Ni Mini level 50-89 (effective band)");
        Check.True(today.Any(e => e.Name == "Wonderland" && e.Time == "Until 23:00"), "Wonderland closes at 23:00 (stock rate)");
        Check.True(week.Any(e => e.Name == "Wonderland" && e.Content.Contains("Instance Caller") && !e.Content.Contains("Event Transporter")), "review fix: Wonderland is entered through the Instance Caller");
        Check.True(board.All(e => e.Name != "Treasure Land"), "review fix: no retail Treasure Land (igg-104) entry");
        Check.True(EventBoardRuntime.SchemaSql.Contains("EXCEPTION WHEN insufficient_privilege"), "review fix: gweditor GRANT is non-fatal for a non-owner");
        Check.True(board.Select((e, i) => (e, i)).Where(p => p.e.List == EventBoardList.Today).All(p => p.i > board.ToList().FindLastIndex(e => e.List == EventBoardList.Week)), "Today is sent after This week");
        Check.True(board.All(e => !e.Name.Contains("Marathon")), "unbuilt events are not generated");

        var friday = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.FromHours(8));
        Rates((ServerRateKeys.WonderlandCloseHour, 24m));
        var fridayToday = BuildAt(friday).Where(e => e.List == EventBoardList.Today).Select(e => $"{e.Time} {e.Name}").ToArray();
        Check.True(fridayToday.Contains("19:00-19:45 Ni Mini Valley") && !fridayToday.Any(t => t.Contains("Pindus")),
            "Friday: Ni Mini 19:00 (stock), no Pindus (RELEASE: Pindus IGG times Wed/Sat/Sun)");
        Check.True(fridayToday.Contains("All day Wonderland"), "close hour 24 = all day");
        Check.True(!fridayToday.Any(t => t.Contains("Spy")), "spies are not listed while event.spy_invasion is 0");
        Check.True(PacketBuilder.EventBoardFrames(BuildAt(friday)).All(f => f.Length == 1894), "every frame 1894 bytes");

        Rates((ServerRateKeys.PindusAlwaysOpen, 1m));
        Check.True(BuildAt(friday).Any(e => e.List == EventBoardList.Week && e.Name == "Pindus Mountains" && e.Time == "Always open"), "always-open rate shown");
        ServerRates.ResetToDefaults();
    }

    private static void CheckCustomRows()
    {
        EventBoardRuntime.InstallForTesting(
        [
            new EventBoardCustomRow(1, "event", 0, "Marathon", 0b1111110, 20 * 60, 21 * 60, "", "Run.", "", "", "", null, Enabled: true),
            new EventBoardCustomRow(2, "notice", 0, "Welcome", 127, null, null, "", "Hello.", "", "", "", null, Enabled: true),
            new EventBoardCustomRow(3, "guide", 0, "Level 30", 127, null, null, "", "Go north.", "", "", "", null, Enabled: true),
            new EventBoardCustomRow(4, "event", 0, "Zeus' Gift", 127, null, null, "", "Off.", "", "", "", "ZeusGift", Enabled: false),
            new EventBoardCustomRow(5, "event", 0, "Hidden", 127, null, null, "", "", "", "", "", null, Enabled: false)
        ]);
        var monday = BuildAt(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8)));
        var sunday = BuildAt(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.FromHours(8)));
        Check.True(monday.Any(e => e.List == EventBoardList.Today && e.Name == "Marathon" && e.Time == "20:00-21:00"), "custom event today on Monday");
        Check.True(!sunday.Any(e => e.List == EventBoardList.Today && e.Name == "Marathon"), "custom event not on Sunday (day mask)");
        Check.True(monday.Any(e => e.List == EventBoardList.Week && e.Name == "Marathon" && e.Time == "Mon Tue Wed Thu Fri Sat 20:00"), "custom week time");
        Check.True(monday.Any(e => e.List == EventBoardList.Notice && e.Name == "Welcome"), "custom notice");
        Check.True(monday.Any(e => e.List == EventBoardList.Guide && e.Name == "Level 30"), "custom guide");
        Check.True(!monday.Any(e => e.Name == "Zeus' Gift"), "a disabled replacing row hides the generated entry");
        Check.True(!monday.Any(e => e.Name == "Hidden"), "disabled rows are not listed");
        EventBoardRuntime.InstallForTesting([]);
    }
}
