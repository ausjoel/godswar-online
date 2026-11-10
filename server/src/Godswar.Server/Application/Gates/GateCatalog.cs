using Godswar.Server.Application.Events;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.Gates;

// FINALISE2 (P1 gates, the admin F-18/F-64): one catalogue and one rule set for every way into a map that has a level band or
// opening times. Editor rows live in public.level_band_overrides (band, schedule_mode, gm_bypass) and
// public.event_schedule_overrides (windows, activity = the gate's window activity). No row = the compiled values, which
// are the admin's 2026-11-05 answers (F-16, F-21, F-67, F-68, F-70, F-19, F-03). Every clock is realm time: AEST on live.

/// <summary>FINALISE2: the realm clock for static policies (spies, the farm round) that are not handed a calendar.</summary>
internal static class RealmClock
{
    private static RealmCalendar? _calendar;

    /// <summary>Set once at start-up from the process realm's persisted calendar (Australia/Brisbane on live).</summary>
    public static RealmCalendar? Calendar
    {
        get => Volatile.Read(ref _calendar);
        set => Volatile.Write(ref _calendar, value);
    }

    /// <summary>The realm's wall clock at <paramref name="instant"/>. Without a calendar (unit checks): UTC.</summary>
    public static DateTimeOffset ToRealmTime(DateTimeOffset instant) =>
        Calendar?.ToRealmTime(instant) ?? instant.ToUniversalTime();
}

internal enum GateSchedule : byte
{
    /// <summary>Today's code path (compiled windows, rates, environment switches).</summary>
    Compiled = 0,
    Always = 1,
    Windows = 2,
    Closed = 3
}

/// <summary>One gate. Levels are 1-200; 200 = no cap.</summary>
internal sealed record GateDefinition(
    string Key,
    string Group,
    string DisplayName,
    string Buttons,
    int CompiledMinimum,
    int CompiledMaximum,
    IReadOnlyList<EventWindow>? CompiledWindows,
    string CompiledSchedule,
    bool CompiledGmBypass,
    bool HasLevelBand,
    string ClientText,
    string Evidence)
{
    /// <summary>event_schedule_overrides.activity for this gate's custom windows.</summary>
    public string WindowActivity { get; init; } = Key;

    /// <summary>Pindus, Ni Mini and Lelantine Farm: editor windows apply without schedule_mode (as before this build).</summary>
    public bool RowsAloneSwitchWindows { get; init; }

    /// <summary>False where a GM flag cannot be honoured (party instances check every member synchronously).</summary>
    public bool SupportsGmBypass { get; init; } = true;
}

internal static class GateKeys
{
    public const string Mycenae = "transport.mycenae";
    public const string Parnassus = "battlefield.parnassus";
    public const string Medusa = "instance.medusa";
    public const string CursedZone2 = "event.cursed_zone2";
    public const string PansLabyrinth = "event.pans_labyrinth";
    public const string TreasureLand = "event.treasure_land"; // PAN CHAIN
    public const string Sicily = "event.sicily"; // PAN CHAIN
    public const string HeraLabyrinth = "event.hera_labyrinth"; // HERA'S LABYRINTH (F-161)
    public const string Spies = "event.spy_invasion";
    public const string ZeusExchange = "event.zeus_gift_exchange";
    public const string ZeusLuck = "event.zeus_gift_luck";
    public const string ZeusClaim = "event.zeus_gift_claim";
}

internal static class GateCatalog
{
    public const int LevelCap = 200;

    /// <summary>F-19: the client's NF_L0_10003 "You need 20,000 Silver!" (the admin: enforce hours and Silver).</summary>
    public const int MycenaeSilverFee = 20_000;

    private static EventWindow Daily(int day, int start, int minutes) => new(day, start, minutes);

    private static IReadOnlyList<EventWindow> EveryDay(int start, int minutes) =>
        Enumerable.Range(0, 7).Select(day => Daily(day, start, minutes)).ToArray();

    /// <summary>Transporter keys by target map: one gate per destination, shared by every button that goes there.</summary>
    public static string? TransporterKey(short targetMapId) => targetMapId switch
    {
        2 or 4 => "transport.suburbs",
        13 => "transport.peloponnesus",
        11 => "transport.marathon",
        5 => "transport.nemea",
        3 => "transport.parnitha",
        14 => "transport.nemea_forest",
        12 => "transport.parnitha_port",
        8 => "transport.thermopylae",
        9 => "transport.thebes",
        6 => GateKeys.Mycenae,
        20 => "transport.delphi",
        7 => "transport.olympia",
        15 => "transport.derveni",
        10 => "transport.larissa",
        22 => "transport.elasson",
        21 => "transport.olympus",
        _ => null
    };

    private static readonly Lazy<IReadOnlyList<GateDefinition>> AllValue = new(Build);

    public static IReadOnlyList<GateDefinition> All => AllValue.Value;

    public static GateDefinition? Find(string key) => All.FirstOrDefault(g => g.Key == key);

    private static int Cap(int maximum) => Math.Min(maximum, LevelCap);

    private static IReadOnlyList<GateDefinition> Build()
    {
        var list = new List<GateDefinition>();

        // Ordinary transporters: one gate per destination, compiled minimum = TransporterProtocol (no cap, no hours).
        foreach (var group in TransporterProtocol.Destinations
                     .GroupBy(d => TransporterKey(d.TargetMapId))
                     .Where(g => g.Key is not null))
        {
            var first = group.First();
            var key = group.Key!;
            var buttons = string.Join(", ", group.Select(d => $"{d.NpcKey} {d.SubId}").Distinct());
            var name = key == "transport.suburbs" ? "Suburbs of Sparta / Athens" : first.DisplayName;
            if (key == GateKeys.Mycenae)
            {
                list.Add(new(key, "Transporter", "Mycenae (map 6)", buttons, first.MinimumLevel, LevelCap,
                    EveryDay(12 * 60, 11 * 60 + 55), "daily 12:00-23:55", false, true,
                    "NF_L0_10002 'Lv 130', NF_L0_10004 'Mycenae opens from 12:00 - 23:55.', NF_L0_10003 'You need 20,000 Silver!'",
                    "the admin F-19: enforce hours and the 20,000 Silver fee. The capitals' direct north buttons skip Mycenae."));
                continue;
            }

            list.Add(new(key, "Transporter", $"{name} (map {first.TargetMapId})", buttons,
                group.Min(d => d.MinimumLevel), LevelCap, null, "always", false, true,
                key switch
                {
                    "transport.suburbs" => "none",
                    "transport.peloponnesus" or "transport.marathon" => "NF_L0_32/33 'Lv30'",
                    "transport.nemea" or "transport.parnitha" => "NF_L0_32/33 'Lv40'",
                    "transport.nemea_forest" or "transport.parnitha_port" => "'Lv70'",
                    "transport.thermopylae" => "'Lv100'",
                    "transport.thebes" => "'Lv130'",
                    _ => "no level text (north, GENERATED levels)"
                },
                "TransporterProtocol.cs; npc_teleport_overrides.minimum_level still applies below a gate row"));
        }

        list.Add(new("battlefield.pindus", "Battlefield", "Pindus Mountains (map 38)", "Sparta_056 251, Athens_056 252",
            31, LevelCap, null, "compiled: IGG Wed 11:00, Sat 21:00, Sun 11:00 for 45 min (battlefield rates)", true, true,
            "client 1.4.1 NF_L0_Y7001 'lv.60-200'; NF_L0_Y7002 IGG times",
            "rate battlefield.pindus_band = 1 makes it 60-200 (live). F-70: the 120 cap of the rate-off band is now 200.")
        { WindowActivity = EventContent.Pindus, RowsAloneSwitchWindows = true });
        list.Add(new(LevelBandGates.NiMiniUpper, "Battlefield", "Ni Mini Valley (map 34, page 273)", "Sparta_056/Athens_056 274 -> 273",
            BattlefieldTransporterProtocol.NiMiniMinimumLevel, BattlefieldTransporterProtocol.NiMiniMaximumLevel, null,
            "compiled: IGG Fri 21:45, Sun 19:00 for 45 min (battlefield rates)", true, true,
            "NF_L0_Y273 'Enter battlefield(60-89)'; NF_L0_2720 lower 50-69, higher 70-89",
            "the admin F-67: 50-89, both brackets in one map.")
        { WindowActivity = EventContent.NiMini, RowsAloneSwitchWindows = true });
        list.Add(new(LevelBandGates.LelantineFarm, "Battlefield", "Lelantine Farm (map 42)", "Sparta_056/Athens_056 282 (1282)",
            BattlefieldTransporterProtocol.LelantineFarmMinimumLevel, BattlefieldTransporterProtocol.LelantineFarmMaximumLevel,
            [Daily(6, 22 * 60 + 45, 45)], "Sat 22:45-23:30", true, true,
            "NF_L0_Y7004 'Saturdays 22:45-23:30'; NF_L0_Y7005 'Level 31 and 130'",
            "the admin F-03/F-04: Saturday 22:45-23:30 AEST, the same window scores the round; F-68: 31-200; F-66: players still inside at the end go to their capital.")
        { WindowActivity = EventContent.LelantineFarm, RowsAloneSwitchWindows = true });
        list.Add(new(LevelBandGates.DuelArena, "Battlefield", "Duel Arena (map 57)", "Sparta_056/Athens_056 1001",
            1, LevelCap, null, "always", true, true, "none", "always open"));
        list.Add(new(GateKeys.Parnassus, "Battlefield", "Parnassus (map 68)", "Battlefield Transporter 1002 (GODSWAR_PARNASSUS=1)",
            ParnassusProtocol.MinimumLevel, ParnassusProtocol.MaximumLevel,
            EveryDay(12 * 60, 6 * 60).Concat(EveryDay(19 * 60, 4 * 60 + 55)).ToArray(), "daily 12:00-18:00 and 19:00-23:55", true, true,
            "NF_L0_1003/1004 'Lv70-89, 12:00-18:00 and 19:00-23:55'", "ParnassusProtocol.cs; GODSWAR_PARNASSUS_ALWAYS_OPEN=1 opens it"));

        list.Add(new(GateKeys.Medusa, "Instance", "Medusa's Island (maps 200/204)", "Instance Caller 11",
            MedusaIslandPolicy.MinimumLevel, LevelCap, null, "always (1 a day)", false, true,
            "NF_L0_R11 'Level 90+'", "MedusaIslandPolicy.cs")
        { SupportsGmBypass = false });
        list.Add(new(LevelBandGates.Atlantis, "Instance", "Atlantis (map 205)", "Instance Caller 14 -> 209/210",
            90, LevelCap, null, "always", false, true, "NF_L0_R14 'Level 90+', R208 'Level 90-140'", "the admin F-21: 90-200")
        { SupportsGmBypass = false });
        list.Add(new(LevelBandGates.Wonderland, "Instance", "Wonderland (map 207)", "Instance Caller 15 -> 211",
            120, LevelCap, null, "compiled: every day until event.wonderland_close_hour (23:00)", false, true,
            "NF_EM_B15 'Level 120+'; NF_EM_T211 'once a day before 23:00'", "the admin F-22: 1 entry a day (legacy_instance_settings)")
        { SupportsGmBypass = false });
        list.Add(new(LevelBandGates.HarborAttack, "Instance", "Bay Under Attack (maps 208/209)", "Instance Caller 16 -> 230",
            InstanceCallerProtocol.HarborAttackMinimumLevel, InstanceCallerProtocol.HarborAttackMaximumLevel, null,
            "always (while instance.harbor_attack_entry = 1)", false, true, "NF_EM_B230 'Level 50+'",
            "the admin BAY2 50-90. The 208/209 map split by the leader's level stays compiled.")
        { SupportsGmBypass = false });
        list.Add(new(LevelBandGates.TrainingGround, "Instance", "Trials of Hercules (Training Ground, map 210)", "Instance Caller 17 -> 231",
            InstanceCallerProtocol.TrainingGroundMinimumLevel, InstanceCallerProtocol.TrainingGroundMaximumLevel, null,
            "always (while instance.training_ground_entry = 1)", false, true, "NF_EM_B17 (no level); IGG acid=166 'Level 60-130'",
            "the admin F-16: 60-200")
        { SupportsGmBypass = false });
        // WARRIOR (F-51): the four Warriors' Training Grounds (root 13), one row each.
        foreach (var warrior in WarriorTrainingGroundPolicy.Definitions)
        {
            list.Add(new(warrior.GateKey, "Instance", $"{warrior.DisplayName} (map {warrior.MapId})",
                $"Instance Caller 13 -> {warrior.PageSubId}",
                warrior.MinimumLevel, WarriorTrainingGroundPolicy.MaximumLevel, null,
                "always (while instance.warrior_training_entry = 1)", false, true,
                $"NF_L0_R{warrior.PageSubId}; wiki 'Training Ground {warrior.Ground}: level {warrior.MinimumLevel}+'",
                "GENERATED cap 200 (the wiki gives floors only)")
            { SupportsGmBypass = false });
        }

        list.Add(new(GateKeys.CursedZone2, "Event", "Cursed Zone 2 (map 29)", "Event Transporter 200",
            CursedLandTransportProtocol.MinimumLevel, LevelCap, null, "always", false, true,
            "NF_L0_31 '55+ 20:00-21:00'; NF_L0_35 'Lv20-75'; IGG 55-130", "the admin F-69: keep 20+"));
        list.Add(new(GateKeys.PansLabyrinth, "Event", "Pan's Labyrinth (map 40)", "Event Transporter 218 (GODSWAR_PAN_LABYRINTH=1)",
            PansLabyrinthProtocol.MinimumLevel, PansLabyrinthProtocol.MaximumLevel,
            [Daily(6, 12 * 60, 6 * 60), Daily(0, 12 * 60, 6 * 60)], "compiled: Sat/Sun 12:00-18:00 (GODSWAR_PAN_WINDOW)", false, true,
            "IGG acid=164 'Lv70-130'", "the admin F-70: caps of 120-140 go to 200"));
        list.Add(Godswar.Server.Application.Troy.TroyEvent.Gate); // W2 F-27 TROY: Thursday 20:00-21:00, Lv50+
        // PAN CHAIN: Event Transporter destinations (each behind its own switch).
        list.Add(new(GateKeys.TreasureLand, "Event", "Treasure Land (map 25 Agate)", "Event Transporter 202 (GODSWAR_TREASURE_LAND=1)",
            EventDestinationsProtocol.TreasureLandMinimumLevel, EventDestinationsProtocol.TreasureLandMaximumLevel,
            [Daily(0, 22 * 60, 60)], "compiled: Sun 22:00-23:00 (GODSWAR_TREASURE_LAND_WINDOW)", false, true,
            "client greeting '60-140 Sun'; IGG acid=104 'Level 60-130'", "the admin F-70: caps of 120-140 go to 200"));
        list.Add(new(GateKeys.Sicily, "Event", "Sicily (map 32)", "Event Transporter 700 (GODSWAR_SICILY=1)",
            EventDestinationsProtocol.SicilyMinimumLevel, EventDestinationsProtocol.SicilyMaximumLevel,
            [Daily(5, 22 * 60, 120)], "compiled: Fri 22:00-24:00 free, other days with a Sicilian Spell (GODSWAR_SICILY_WINDOW)", false, true,
            "client greeting '25-140 Fri'; IGG acid=122 'Level 25+'", "the admin F-70: caps of 120-140 go to 200"));
        // HERA'S LABYRINTH (F-161): Event Transporter 500 (Athens) / 501 (Sparta), behind its own switch.
        list.Add(new(GateKeys.HeraLabyrinth, "Event", "Hera's Labyrinth (maps 30 / 31)", "Event Transporter 500 / 501 (GODSWAR_HERA_LABYRINTH=1)",
            HeraLabyrinthProtocol.MinimumLevel, HeraLabyrinthProtocol.MaximumLevel, null, "always (IGG 'All day'; GODSWAR_HERA_LABYRINTH_WINDOW=never closes it)",
            false, true, "IGG acid=121 'After reaching Level 50'; quest 275/1275 Lv50-120", "the admin F-70: caps of 120-140 go to 200"));
        list.Add(new(GateKeys.Spies, "Event", "Spy invasion (capitals)", "monster waves, rate event.spy_invasion = 1",
            1, LevelCap, [Daily(1, 19 * 60, 30), Daily(3, 19 * 60, 30), Daily(6, 19 * 60, 30)],
            "Mon/Wed/Sat 19:00-19:30, a wave every 5 minutes", false, false, "none",
            "the admin F-04: AEST (was a fixed UTC+8 clock). Level fields do not apply."));
        list.Add(new(GateKeys.ZeusExchange, "Event", "Zeus' Gift weekend exchange", "Zeus' Loyal Believer",
            1, LevelCap, null, "compiled: Sat 12:00 - Sun 23:55", false, false, "NF_L0_Z1110", "ZeusGiftPolicy.IsExchangeWindow (F-64)"));
        list.Add(new(GateKeys.ZeusLuck, "Event", "Zeus' Gift luck contest", "Zeus' Loyal Believer",
            1, LevelCap, null, "compiled: Sat 12:00 - Sun 12:00", false, false, "NF_Z_T2", "ZeusGiftPolicy.IsLuckWindow (F-64)"));
        list.Add(new(GateKeys.ZeusClaim, "Event", "Zeus' Gift results claim", "Zeus' Loyal Believer",
            1, LevelCap, null, "compiled: Sun 12:00-23:55", false, false, "NF_Z_T3017", "ZeusGiftPolicy.IsClaimWindow (F-64)"));
        return list;
    }
}

internal readonly record struct GateBand(int Minimum, int Maximum, string MinimumSource, string MaximumSource);

/// <summary>FINALISE2: the one place that decides a gate's band, schedule and GM rule.</summary>
internal static class GateRules
{
    private static LevelBandOverride? Row(string key) =>
        NpcContentOverrides.Current.LevelBands.TryGetValue(key, out var row) ? row : null;

    public static GateSchedule Mode(string key) => Row(key)?.ScheduleMode switch
    {
        "always" => GateSchedule.Always,
        "windows" => GateSchedule.Windows,
        "closed" => GateSchedule.Closed,
        _ => GateSchedule.Compiled
    };

    /// <summary>The band: editor row side, else <paramref name="baseMinimum"/>/<paramref name="baseMaximum"/> (the caller's compiled, rate or button value).</summary>
    public static GateBand Band(string key, int baseMinimum, int baseMaximum, string baseSource = "compiled")
    {
        var row = Row(key);
        return new(
            row?.MinimumLevel ?? baseMinimum,
            row?.MaximumLevel ?? baseMaximum,
            row?.MinimumLevel is null ? baseSource : "editor",
            row?.MaximumLevel is null ? baseSource : "editor");
    }

    /// <summary>The editor's windows for this gate, or null when they do not apply.</summary>
    public static IReadOnlyList<EventWindow>? EditedWindows(string key)
    {
        var definition = GateCatalog.Find(key);
        var activity = definition?.WindowActivity ?? key;
        if (!NpcContentOverrides.Current.Events.Windows.TryGetValue(activity, out var windows))
        {
            return Mode(key) == GateSchedule.Windows ? [] : null;
        }

        return Mode(key) == GateSchedule.Windows || definition?.RowsAloneSwitchWindows == true && Mode(key) == GateSchedule.Compiled
            ? windows
            : null;
    }

    /// <summary>
    /// Whether the gate is open at realm time <paramref name="realmNow"/>. <paramref name="compiled"/> is the caller's
    /// own code path (rates, environment switches), used in mode "compiled" when no editor windows apply.
    /// </summary>
    public static bool IsOpen(string key, DateTime realmNow, Func<bool> compiled)
    {
        switch (Mode(key))
        {
            case GateSchedule.Always:
                return true;
            case GateSchedule.Closed:
                return false;
        }

        var edited = EditedWindows(key);
        return edited is not null ? EventSchedulePolicy.IsOpen(edited, realmNow) : compiled();
    }

    /// <summary>The compiled windows of a catalogue gate, open at <paramref name="realmNow"/>.</summary>
    public static bool CompiledWindowsOpen(string key, DateTime realmNow) =>
        GateCatalog.Find(key)?.CompiledWindows is not { } windows || EventSchedulePolicy.IsOpen(windows, realmNow);

    /// <summary>
    /// The windows a round clock should use (Lelantine Farm, spies): the editor's when they apply and are not empty,
    /// else the compiled ones.
    /// </summary>
    public static IReadOnlyList<EventWindow> RoundWindows(string key)
    {
        var edited = EditedWindows(key);
        return edited is { Count: > 0 } && edited.Any(static w => w.Day != EventContent.ClosedDay)
            ? edited
            : GateCatalog.Find(key)?.CompiledWindows ?? [];
    }

    /// <summary>True when a GM skips this gate: gm.obey_gates = 0 (the admin F-17) and the gate allows it.</summary>
    public static bool GmMaySkip(string key)
    {
        if (ServerRates.Current.Absolute(ServerRateKeys.GmObeyGates) == 1)
        {
            return false;
        }

        var definition = GateCatalog.Find(key);
        if (definition is { SupportsGmBypass: false })
        {
            return false;
        }

        return Row(key)?.GmBypass ?? definition?.CompiledGmBypass ?? false;
    }

    /// <summary>The latest window start at or before <paramref name="realmNow"/> (realm wall clock), within 8 days.</summary>
    public static (DateTime Start, DateTime End)? LatestWindow(IReadOnlyList<EventWindow> windows, DateTime realmNow)
    {
        (DateTime Start, DateTime End)? best = null;
        for (var back = 0; back <= 8; back++)
        {
            var day = realmNow.Date.AddDays(-back);
            foreach (var window in windows)
            {
                if (window.Day == EventContent.ClosedDay || (int)day.DayOfWeek != window.Day)
                {
                    continue;
                }

                var start = day.AddMinutes(window.StartMinute);
                if (start <= realmNow && (best is null || start > best.Value.Start))
                {
                    best = (start, start.AddMinutes(window.DurationMinutes));
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return best;
    }

    /// <summary>The next window start strictly after <paramref name="realmNow"/>, within 8 days.</summary>
    public static DateTime? NextOpening(IReadOnlyList<EventWindow> windows, DateTime realmNow)
    {
        DateTime? best = null;
        for (var ahead = 0; ahead <= 8; ahead++)
        {
            var day = realmNow.Date.AddDays(ahead);
            foreach (var window in windows)
            {
                if (window.Day == EventContent.ClosedDay || (int)day.DayOfWeek != window.Day)
                {
                    continue;
                }

                var start = day.AddMinutes(window.StartMinute);
                if (start > realmNow && (best is null || start < best))
                {
                    best = start;
                }
            }
        }

        return best;
    }

    public static string Describe(GateBand band) =>
        $"{band.Minimum}-{(band.Maximum >= GateCatalog.LevelCap ? "200" : band.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
}
