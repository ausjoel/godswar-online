using System.Text.Json;
using Godswar.Server.Application.Events;
using Godswar.Server.Application.Gates;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Application.ZeusGift;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

// FINALISE2 (P1 gates, the admin F-18/F-64/F-16/F-21/F-67/F-68/F-70/F-19/F-03/F-04): the Gates table.
internal static class GateChecks
{
    public const string CheckName = "FINALISE2 gates: bands, schedule modes, GM rule, AEST";

    private static readonly RealmCalendar Brisbane = RealmCalendar.CreateForTesting(RealmId.Tempest, "Australia/Brisbane");

    // 2026-11-05 is a Thursday; AEST = UTC+10.
    private static DateTimeOffset Aest(int day, int hour, int minute) =>
        new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(10));

    public static Task RunAsync()
    {
        try
        {
            RealmClock.Calendar = Brisbane;
            CheckCatalogue();
            CheckDefaultsAreAdminsAnswers();
            CheckBandOverrides();
            CheckScheduleModes();
            CheckGmRule();
            CheckFarmOneClock();
            CheckMycenaeHours();
            CheckZeusOverride();
            CheckDescribe();
        }
        finally
        {
            RealmClock.Calendar = null;
            ServerRates.ResetToDefaults();
            NpcContentOverrides.Install(NpcContentSnapshot.Empty);
        }

        return Task.CompletedTask;
    }

    private static void Install(
        IReadOnlyDictionary<string, LevelBandOverride>? bands = null,
        IReadOnlyDictionary<string, IReadOnlyList<EventWindow>>? windows = null) =>
        NpcContentOverrides.Install(new NpcContentSnapshot(
            1,
            new Dictionary<string, IReadOnlyList<NpcShopOverrideRow>>(),
            [],
            events: new EventContent(windows ?? new Dictionary<string, IReadOnlyList<EventWindow>>(), [], EventContent.DefaultPopulationMinutes),
            levelBands: bands));

    private static void Rates(params (string Key, decimal Value)[] values) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            values.Select(v => KeyValuePair.Create(v.Key, v.Value)), 1, DateTimeOffset.UtcNow, "check", "test"));

    private static void CheckCatalogue()
    {
        var keys = GateCatalog.All.Select(g => g.Key).ToArray();
        Check.True(keys.Length == keys.Distinct().Count(), "gate keys are unique");
        foreach (var expected in new[]
                 {
                     "transport.suburbs", "transport.marathon", "transport.peloponnesus", "transport.parnitha", "transport.nemea",
                     "transport.parnitha_port", "transport.nemea_forest", "transport.thermopylae", "transport.thebes",
                     GateKeys.Mycenae, "transport.delphi", "transport.olympia", "transport.derveni", "transport.larissa",
                     "transport.elasson", "transport.olympus", LevelBandGates.Pindus, LevelBandGates.NiMiniUpper,
                     LevelBandGates.LelantineFarm, LevelBandGates.DuelArena, GateKeys.Parnassus, GateKeys.Medusa,
                     LevelBandGates.Atlantis, LevelBandGates.Wonderland, LevelBandGates.HarborAttack, LevelBandGates.TrainingGround,
                     GateKeys.CursedZone2, GateKeys.PansLabyrinth, GateKeys.Spies, GateKeys.ZeusExchange, GateKeys.ZeusLuck, GateKeys.ZeusClaim
                 })
        {
            Check.True(keys.Contains(expected), $"catalogue has {expected}");
        }

        foreach (var gate in GateCatalog.All)
        {
            Check.True(gate.Key.Length is >= 3 and <= 64 && gate.Key == gate.Key.ToLowerInvariant(), $"{gate.Key} is a valid key");
            Check.True(gate.CompiledMinimum >= 1 && gate.CompiledMaximum <= 200 && gate.CompiledMinimum <= gate.CompiledMaximum,
                $"{gate.Key} compiled band {gate.CompiledMinimum}-{gate.CompiledMaximum} is inside 1-200");
        }

        foreach (var destination in TransporterProtocol.Destinations)
        {
            Check.True(GateCatalog.TransporterKey(destination.TargetMapId) is not null,
                $"transporter button {destination.NpcKey} {destination.SubId} ({destination.DisplayName}) has a gate");
        }
    }

    private static void CheckDefaultsAreAdminsAnswers()
    {
        Install();
        Rates();
        static (int, int) Compiled(string key) =>
            GateCatalog.Find(key) is { } g ? (g.CompiledMinimum, g.CompiledMaximum) : (-1, -1);
        Check.Equal((60, 200), Compiled(LevelBandGates.TrainingGround), "F-16 Trials of Hercules 60-200");
        Check.Equal((90, 200), Compiled(LevelBandGates.Atlantis), "F-21 Atlantis 90-200");
        Check.Equal((50, 89), Compiled(LevelBandGates.NiMiniUpper), "F-67 Ni Mini 50-89, one map");
        Check.Equal((31, 200), Compiled(LevelBandGates.LelantineFarm), "F-68 Lelantine Farm 31-200");
        Check.Equal((70, 200), Compiled(GateKeys.PansLabyrinth), "F-70 Pan's Labyrinth cap 140 -> 200");
        Check.Equal((31, 200), Compiled(LevelBandGates.Pindus), "F-70 Pindus rate-off cap 120 -> 200");
        Check.Equal((20, 200), Compiled(GateKeys.CursedZone2), "F-69 Cursed Zone 2 stays 20+");
        Check.Equal((130, 200), Compiled(GateKeys.Mycenae), "Mycenae Lv130");
        Check.Equal(20_000, GateCatalog.MycenaeSilverFee, "F-19 Mycenae 20,000 Silver");
        Check.Equal(1, (int)LegacyInstanceDailyEntryPolicy.GetDefaultFreeEntryLimit(InstanceCallerEntryKind.Wonderland), "F-22 Wonderland one free entry");
        Check.True(LegacyInstanceDailyEntryPolicy.GetDefaultPaidRetryLimit(InstanceCallerEntryKind.Wonderland) == 0, "F-22 no paid Wonderland retry");
        // No gate keeps a 120-140 cap (F-70), except Bay 90 and Ni Mini / Parnassus 89, which are below the range.
        foreach (var gate in GateCatalog.All.Where(g => g.HasLevelBand))
        {
            Check.True(gate.CompiledMaximum is < 120 or > 140, $"F-70: {gate.Key} has no 120-140 cap ({gate.CompiledMaximum})");
        }

        Check.True(GateRules.Mode(LevelBandGates.TrainingGround) == GateSchedule.Compiled, "no row = compiled mode");
    }

    private static void CheckBandOverrides()
    {
        Install(new Dictionary<string, LevelBandOverride>
        {
            [LevelBandGates.TrainingGround] = new(100, 150),
            [LevelBandGates.Atlantis] = new(null, 120),
            ["transport.thebes"] = new(150, null)
        });
        var tg = GateRules.Band(LevelBandGates.TrainingGround, 60, 200);
        Check.True(tg is { Minimum: 100, Maximum: 150, MinimumSource: "editor", MaximumSource: "editor" }, $"both sides edited: {tg}");
        var at = GateRules.Band(LevelBandGates.Atlantis, 90, 200);
        Check.True(at is { Minimum: 90, Maximum: 120, MinimumSource: "compiled", MaximumSource: "editor" }, $"a NULL side keeps the compiled value: {at}");
        var th = GateRules.Band("transport.thebes", 145, 200, "npc_teleport_overrides");
        Check.True(th is { Minimum: 150, MinimumSource: "editor" }, $"a gate row beats the button override: {th}");
        var none = GateRules.Band("transport.marathon", 30, 200);
        Check.True(none is { Minimum: 30, Maximum: 200, MinimumSource: "compiled" }, $"no row = compiled: {none}");
        Install();
    }

    private static void CheckScheduleModes()
    {
        var thursdayNoon = Aest(5, 12, 0).DateTime;
        Install(new Dictionary<string, LevelBandOverride> { [GateKeys.CursedZone2] = new(null, null, "closed") });
        Check.True(!GateRules.IsOpen(GateKeys.CursedZone2, thursdayNoon, static () => true), "mode closed refuses");
        Install(new Dictionary<string, LevelBandOverride> { [GateKeys.Mycenae] = new(null, null, "always") });
        Check.True(GateRules.IsOpen(GateKeys.Mycenae, Aest(5, 3, 0).DateTime, static () => false), "mode always admits at 03:00");
        // Windows mode: Thursday 23:30 for 60 minutes (runs past midnight).
        Install(
            new Dictionary<string, LevelBandOverride> { [GateKeys.CursedZone2] = new(null, null, "windows") },
            new Dictionary<string, IReadOnlyList<EventWindow>> { [GateKeys.CursedZone2] = [new(4, 23 * 60 + 30, 60)] });
        Check.True(!GateRules.IsOpen(GateKeys.CursedZone2, Aest(5, 23, 29).DateTime, static () => true), "windows: closed before 23:30");
        Check.True(GateRules.IsOpen(GateKeys.CursedZone2, Aest(5, 23, 45).DateTime, static () => false), "windows: open 23:45");
        Check.True(GateRules.IsOpen(GateKeys.CursedZone2, Aest(6, 0, 15).DateTime, static () => false), "windows: still open past midnight");
        Check.True(!GateRules.IsOpen(GateKeys.CursedZone2, Aest(6, 0, 30).DateTime, static () => true), "windows: closed at 00:30");
        // Windows rows alone do nothing for an ordinary gate (mode must be "windows")...
        Install(windows: new Dictionary<string, IReadOnlyList<EventWindow>> { [GateKeys.CursedZone2] = [new(4, 0, 1)] });
        Check.True(GateRules.IsOpen(GateKeys.CursedZone2, thursdayNoon, static () => true), "rows without mode windows are ignored");
        // ...but switch Pindus, Ni Mini and Lelantine Farm on their own, as before this build.
        Install(windows: new Dictionary<string, IReadOnlyList<EventWindow>> { [EventContent.NiMini] = [new(4, 11 * 60, 120)] });
        Check.True(BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.NiMiniUpper, Brisbane, Aest(5, 12, 0)), "Ni Mini rows alone open it Thursday 12:00");
        Check.True(!BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.NiMiniUpper, Brisbane, Aest(5, 13, 30)), "and close it at 13:00");
        // Instances: closed mode = Closed status (Wonderland keeps its own cut-off line).
        Install(new Dictionary<string, LevelBandOverride>
        {
            [LevelBandGates.Atlantis] = new(null, null, "closed"),
            [LevelBandGates.Wonderland] = new(null, null, "closed")
        });
        Check.True(LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.Atlantis, Brisbane, Aest(5, 12, 0)) == LegacyInstanceScheduleStatus.Closed, "Atlantis closed by the editor");
        Check.True(LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.Wonderland, Brisbane, Aest(5, 12, 0)) == LegacyInstanceScheduleStatus.DailyCutoffPassed, "Wonderland closed answers its own line");
        Install();
        Check.True(LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.TrainingGround, Brisbane, Aest(5, 3, 0)) == LegacyInstanceScheduleStatus.Open, "Trials of Hercules compiled = always open");
        Check.True(LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.Wonderland, Brisbane, Aest(5, 23, 15)) == LegacyInstanceScheduleStatus.DailyCutoffPassed, "Wonderland compiled cut-off 23:00 AEST");
    }

    private static void CheckGmRule()
    {
        Install();
        Rates();
        Check.True(GateRules.GmMaySkip(LevelBandGates.NiMiniUpper), "battlefields: GMs skip by default (gm.obey_gates = 0)");
        Check.True(GateRules.GmMaySkip(GateKeys.Parnassus), "Parnassus: GMs skip by default");
        Check.True(!GateRules.GmMaySkip(GateKeys.Mycenae), "ordinary transporters: no GM skip (as before)");
        Check.True(!GateRules.GmMaySkip(LevelBandGates.TrainingGround), "instances: no GM skip");
        Rates((ServerRateKeys.GmObeyGates, 1m));
        Check.True(!GateRules.GmMaySkip(LevelBandGates.NiMiniUpper) && !GateRules.GmMaySkip(GateKeys.Parnassus),
            "gm.obey_gates = 1: GMs obey the battlefield and Parnassus gates too");
        Rates();
        Install(new Dictionary<string, LevelBandOverride>
        {
            [GateKeys.Mycenae] = new(null, null, GmBypass: true),
            [LevelBandGates.NiMiniUpper] = new(null, null, GmBypass: false),
            [LevelBandGates.TrainingGround] = new(null, null, GmBypass: true)
        });
        Check.True(GateRules.GmMaySkip(GateKeys.Mycenae), "a row can let GMs skip a transporter");
        Check.True(!GateRules.GmMaySkip(LevelBandGates.NiMiniUpper), "a row can make GMs obey one battlefield");
        Check.True(!GateRules.GmMaySkip(LevelBandGates.TrainingGround), "party instances never take a GM skip (per-member checks)");
        Install();
    }

    private static void CheckFarmOneClock()
    {
        Install();
        Rates();
        // Compiled: Saturday 22:45-23:30 AEST, for the gate and the round.
        foreach (var (instant, open) in new[]
                 {
                     (Aest(7, 22, 44), false), (Aest(7, 22, 45), true), (Aest(7, 23, 29), true), (Aest(7, 23, 30), false),
                     (Aest(8, 0, 50), false)
                 })
        {
            Check.True(BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.LelantineFarm, Brisbane, instant) == open &&
                LelantineFarmRoundPolicy.IsOpen(instant) == open,
                $"farm gate and round agree at {instant:ddd HH:mm} AEST: {open}");
        }

        // An editor window (Thursday 20:00 for 30 min) moves the gate AND the round.
        Install(windows: new Dictionary<string, IReadOnlyList<EventWindow>> { [EventContent.LelantineFarm] = [new(4, 20 * 60, 30)] });
        Check.True(BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.LelantineFarm, Brisbane, Aest(5, 20, 10)), "edited farm window opens the gate");
        Check.Equal(Aest(5, 20, 0).ToUniversalTime(), LelantineFarmRoundPolicy.LatestWindow(Aest(5, 20, 10)).Start, "and the round starts with it");
        Check.True(!LelantineFarmRoundPolicy.IsOpen(Aest(7, 22, 50)), "the compiled Saturday round no longer applies");
        // Always open (rate): the gate opens, the round keeps its windows.
        Install();
        Rates((ServerRateKeys.LelantineAlwaysOpen, 1m));
        Check.True(BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.LelantineFarm, Brisbane, Aest(5, 12, 0)), "always-open rate opens the farm");
        Check.True(!LelantineFarmRoundPolicy.IsOpen(Aest(5, 12, 0)), "the round still scores only in its window");
        Rates();
    }

    private static void CheckMycenaeHours()
    {
        Install();
        Check.True(GateSchedules.IsOpen(GateKeys.Mycenae, Brisbane, Aest(5, 11, 59)) == false, "Mycenae closed at 11:59 AEST");
        Check.True(GateSchedules.IsOpen(GateKeys.Mycenae, Brisbane, Aest(5, 12, 0)), "Mycenae opens at 12:00 AEST");
        Check.True(GateSchedules.IsOpen(GateKeys.Mycenae, Brisbane, Aest(5, 23, 54)), "open at 23:54");
        Check.True(!GateSchedules.IsOpen(GateKeys.Mycenae, Brisbane, Aest(5, 23, 55)), "closed at 23:55");
        Check.True(GateSchedules.IsOpen("transport.olympia", Brisbane, Aest(5, 3, 0)), "other transporters have no hours");
        Check.True(GateSchedules.IsOpen(GateKeys.Parnassus, Brisbane, Aest(5, 12, 30)) &&
            !GateSchedules.IsOpen(GateKeys.Parnassus, Brisbane, Aest(5, 18, 30)), "Parnassus periods unchanged (AEST)");
    }

    private static void CheckZeusOverride()
    {
        Install();
        var thursday = new DateOnly(2026, 11, 5);
        Check.True(!ZeusGiftPolicy.IsExchangeWindow(thursday, new TimeOnly(12, 0)), "Zeus exchange compiled: closed Thursday");
        Install(new Dictionary<string, LevelBandOverride> { [GateKeys.ZeusExchange] = new(null, null, "always") });
        Check.True(ZeusGiftPolicy.IsExchangeWindow(thursday, new TimeOnly(12, 0)), "Zeus exchange mode always: open Thursday");
        Install(new Dictionary<string, LevelBandOverride> { [GateKeys.ZeusClaim] = new(null, null, "closed") });
        Check.True(!ZeusGiftPolicy.IsClaimWindow(new DateOnly(2026, 11, 8), new TimeOnly(13, 0)), "Zeus claim mode closed: closed Sunday 13:00");
        Install();
        Check.True(ZeusGiftPolicy.IsClaimWindow(new DateOnly(2026, 11, 8), new TimeOnly(13, 0)), "Zeus claim compiled: open Sunday 13:00");
    }

    private static void CheckDescribe()
    {
        Install(new Dictionary<string, LevelBandOverride> { [LevelBandGates.TrainingGround] = new(70, null, "closed", null) });
        Rates((ServerRateKeys.BattlefieldPindusBand, 1m));
        using var json = JsonDocument.Parse(GateSchedules.Describe(Brisbane, Aest(5, 12, 0)));
        var root = json.RootElement;
        Check.Equal("Australia/Brisbane", root.GetProperty("time_zone").GetString(), "describe: realm time zone");
        var gates = root.GetProperty("gates").EnumerateArray().ToDictionary(g => g.GetProperty("key").GetString()!);
        Check.Equal(GateCatalog.All.Count, gates.Count, "describe: every gate");
        var tg = gates[LevelBandGates.TrainingGround];
        Check.True(tg.GetProperty("effective").GetProperty("minimum").GetInt32() == 70 &&
            tg.GetProperty("effective").GetProperty("minimum_source").GetString() == "editor" &&
            tg.GetProperty("effective").GetProperty("maximum").GetInt32() == 200 &&
            !tg.GetProperty("open_now").GetBoolean(), "describe: Trials row 70-(200), closed");
        var pindus = gates[LevelBandGates.Pindus];
        Check.True(pindus.GetProperty("compiled").GetProperty("minimum").GetInt32() == 60 &&
            pindus.GetProperty("compiled").GetProperty("source").GetString() == "rate battlefield.pindus_band",
            "describe: Pindus baseline follows the rate");
        Check.True(gates[GateKeys.Mycenae].GetProperty("open_now").GetBoolean(), "describe: Mycenae open at 12:00");
        Check.True(json.RootElement.GetRawText().Length < 60_000, "describe fits the 64 KiB management response");
    }
}
