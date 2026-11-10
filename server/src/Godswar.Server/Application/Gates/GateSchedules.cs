using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Events;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Application.ZeusGift;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.Gates;

/// <summary>
/// FINALISE2 (the admin F-18/F-64): "is this gate open now" for every catalogue key, from the same code the callers use, so
/// the editor's describe page, the event board and the gates cannot disagree.
/// </summary>
internal static class GateSchedules
{
    /// <summary>GODSWAR_PARNASSUS_ALWAYS_OPEN=1 ignores the Parnassus periods (rig switch, unchanged).</summary>
    public static bool ParnassusAlwaysOpen =>
        Environment.GetEnvironmentVariable("GODSWAR_PARNASSUS_ALWAYS_OPEN") is "1" or "true";

    public static bool IsOpen(string key, RealmCalendar calendar, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var realm = calendar.ToRealmTime(instant).DateTime;
        // WARRIOR (F-51): the four Warriors' Training Grounds follow their own instance kind.
        if (WarriorTrainingGroundPolicy.Definitions.FirstOrDefault(ground => ground.GateKey == key) is { } warrior)
        {
            return LegacyInstanceAdmissionPolicy.CheckSchedule(warrior.EntryKind, calendar, instant) == LegacyInstanceScheduleStatus.Open;
        }
        switch (key)
        {
            case "battlefield.pindus":
                return BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.Pindus, calendar, instant);
            case LevelBandGates.NiMiniUpper:
            case LevelBandGates.NiMiniLower:
                return BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.NiMiniUpper, calendar, instant);
            case LevelBandGates.LelantineFarm:
                return BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.LelantineFarm, calendar, instant);
            case LevelBandGates.DuelArena:
                return BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.DuelArena, calendar, instant);
            case LevelBandGates.Atlantis:
                return LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.Atlantis, calendar, instant) == LegacyInstanceScheduleStatus.Open;
            case LevelBandGates.Wonderland:
                return LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.Wonderland, calendar, instant) == LegacyInstanceScheduleStatus.Open;
            case LevelBandGates.HarborAttack:
                return LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.HarborAttack, calendar, instant) == LegacyInstanceScheduleStatus.Open;
            case LevelBandGates.TrainingGround:
                return LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.TrainingGround, calendar, instant) == LegacyInstanceScheduleStatus.Open;
            case GateKeys.Parnassus:
                return GateRules.IsOpen(key, realm, () => ParnassusAlwaysOpen || ParnassusProtocol.IsOpen(TimeOnly.FromDateTime(realm)));
            case GateKeys.PansLabyrinth:
                return GateRules.IsOpen(key, realm, () => PansLabyrinthProtocol.IsOpenAt(realm));
            case GateKeys.TreasureLand:
                return GateRules.IsOpen(key, realm, () => EventDestinationsProtocol.TreasureLandOpenAt(realm));
            case GateKeys.Sicily:
                return GateRules.IsOpen(key, realm, () => EventDestinationsProtocol.SicilyFreeAt(realm));
            case GateKeys.HeraLabyrinth:
                return GateRules.IsOpen(key, realm, () => HeraLabyrinthProtocol.IsOpenAt(realm));
            case GateKeys.Spies:
                return SpyInvasionPolicy.IsActive(instant, calendar);
            case GateKeys.ZeusExchange:
                return ZeusGiftPolicy.IsExchangeWindow(DateOnly.FromDateTime(realm), TimeOnly.FromDateTime(realm));
            case GateKeys.ZeusLuck:
                return ZeusGiftPolicy.IsLuckWindow(DateOnly.FromDateTime(realm), TimeOnly.FromDateTime(realm));
            case GateKeys.ZeusClaim:
                return ZeusGiftPolicy.IsClaimWindow(DateOnly.FromDateTime(realm), TimeOnly.FromDateTime(realm));
            default:
                // Ordinary transporters (Mycenae has compiled windows), Medusa, Cursed Zone 2.
                return GateRules.IsOpen(key, realm, () => GateRules.CompiledWindowsOpen(key, realm));
        }
    }

    /// <summary>
    /// The day/time-based Zeus' Gift windows read the gate first: "compiled" keeps the coded rule, the other modes
    /// replace it (F-64).
    /// </summary>
    public static bool? ZeusOverride(string key, DateOnly realmDay, TimeOnly realmTime)
    {
        var realm = realmDay.ToDateTime(realmTime);
        if (GateRules.Mode(key) == GateSchedule.Compiled && GateRules.EditedWindows(key) is null)
        {
            return null;
        }

        return GateRules.IsOpen(key, realm, static () => false);
    }

    /// <summary>Pindus / Ni Mini compiled windows (IGG times switch, window length and shift rates), realm time.</summary>
    private static IReadOnlyList<EventWindow> CompiledBattlefieldWindows(BattlefieldDestinationKind kind)
    {
        var rates = ServerRates.Current;
        var minutes = (int)rates.Absolute(ServerRateKeys.BattlefieldWindowMinutes);
        var shift = (int)rates.Absolute(ServerRateKeys.BattlefieldShiftHours) * 60;
        return Enum.GetValues<DayOfWeek>()
            .SelectMany(day => BattlefieldSchedulePolicy.Starts(kind, day).Select(start =>
            {
                var absolute = ((int)day * 1440 + start.Hour * 60 + start.Minute + shift + 10080) % 10080;
                return new EventWindow(absolute / 1440, absolute % 1440, minutes);
            }))
            .ToArray();
    }

    private static object Windows(IEnumerable<EventWindow>? windows) =>
        (windows ?? []).Select(static w => new { day = w.Day, start_minute = w.StartMinute, duration_minutes = w.DurationMinutes }).ToArray();

    /// <summary>GET /gate-content: compiled and effective values of every gate, for the editor's Gates page.</summary>
    public static string Describe(RealmCalendar? calendar, DateTimeOffset now)
    {
        calendar ??= RealmCalendar.CreateForTesting(Godswar.Server.Domain.World.Instances.RealmId.Tempest);
        var realm = calendar.ToRealmTime(now).DateTime;
        var rates = ServerRates.Current;
        var pindusBand = BattlefieldRewardPolicy.PindusBandEnabled;
        var gates = GateCatalog.All.Select(g =>
        {
            var baseMinimum = g.CompiledMinimum;
            var baseMaximum = Math.Min(g.CompiledMaximum, GateCatalog.LevelCap);
            var baseSource = "compiled";
            if (g.Key == "battlefield.pindus" && pindusBand)
            {
                baseMinimum = BattlefieldRewardPolicy.PindusBandMinimumLevel;
                baseMaximum = BattlefieldRewardPolicy.PindusBandMaximumLevel;
                baseSource = "rate battlefield.pindus_band";
            }

            var band = GateRules.Band(g.Key, baseMinimum, Math.Min(baseMaximum, GateCatalog.LevelCap), baseSource);
            NpcContentOverrides.Current.LevelBands.TryGetValue(g.Key, out var row);
            var hasRow = NpcContentOverrides.Current.LevelBands.ContainsKey(g.Key);
            NpcContentOverrides.Current.Events.Windows.TryGetValue(g.WindowActivity, out var windowRows);
            var edited = GateRules.EditedWindows(g.Key);
            var open = IsOpen(g.Key, calendar, now);
            // Pindus / Ni Mini compile their windows from the battlefield rates; show them like the event board does.
            var compiledWindows = g.CompiledWindows ?? (g.Key == LevelBandGates.Pindus || g.Key == LevelBandGates.NiMiniUpper
                ? CompiledBattlefieldWindows(g.Key == LevelBandGates.Pindus ? BattlefieldDestinationKind.Pindus : BattlefieldDestinationKind.NiMiniUpper)
                : null);
            var next = GateRules.Mode(g.Key) is GateSchedule.Always or GateSchedule.Closed
                ? null
                : GateRules.NextOpening(edited ?? compiledWindows ?? [], realm);
            return new
            {
                key = g.Key,
                group = g.Group,
                name = g.DisplayName,
                buttons = g.Buttons,
                has_level_band = g.HasLevelBand,
                supports_gm_bypass = g.SupportsGmBypass,
                window_activity = g.WindowActivity,
                rows_alone_switch_windows = g.RowsAloneSwitchWindows,
                compiled = new
                {
                    minimum = baseMinimum,
                    maximum = Math.Min(baseMaximum, GateCatalog.LevelCap),
                    source = baseSource,
                    schedule = g.CompiledSchedule,
                    windows = Windows(compiledWindows),
                    gm_bypass = g.CompiledGmBypass
                },
                row = hasRow
                    ? new
                    {
                        minimum = row.MinimumLevel,
                        maximum = row.MaximumLevel,
                        schedule_mode = row.ScheduleMode,
                        gm_bypass = row.GmBypass
                    }
                    : null,
                window_rows = Windows(windowRows),
                effective = new
                {
                    minimum = band.Minimum,
                    maximum = Math.Min(band.Maximum, GateCatalog.LevelCap),
                    minimum_source = band.MinimumSource,
                    maximum_source = band.MaximumSource,
                    schedule_mode = GateRules.Mode(g.Key).ToString().ToLowerInvariant(),
                    windows_apply = edited is not null,
                    gm_skips = GateRules.GmMaySkip(g.Key)
                },
                client_text = g.ClientText,
                evidence = g.Evidence,
                open_now = open,
                next_opening = next?.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            };
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            revision = NpcContentOverrides.Current.Revision,
            realm_time = realm.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            time_zone = calendar.TimeZoneId,
            gm_obey_gates = rates.Absolute(ServerRateKeys.GmObeyGates),
            mycenae_silver = GateCatalog.MycenaeSilverFee,
            gates
        });
    }
}
