using Godswar.Server.Application.Events;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Rates; // ROUND2
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Realm-calendar admission windows advertised by the current event panel.
/// A window is start-inclusive and end-exclusive.
/// </summary>
internal static class BattlefieldSchedulePolicy
{
    private static readonly TimeSpan EventDuration =
        TimeSpan.FromMinutes(45);

    /// <summary>FINALISE2: the gate key of each battlefield destination (Gates table).</summary>
    public static string GateKey(BattlefieldDestinationKind destination) => destination switch
    {
        BattlefieldDestinationKind.Pindus => LevelBandGates.Pindus,
        BattlefieldDestinationKind.NiMiniLower => LevelBandGates.NiMiniUpper, // one map, one gate (the admin F-67)
        BattlefieldDestinationKind.NiMiniUpper => LevelBandGates.NiMiniUpper,
        BattlefieldDestinationKind.LelantineFarm => LevelBandGates.LelantineFarm,
        _ => LevelBandGates.DuelArena
    };

    public static bool IsOpen(
        BattlefieldDestinationKind destination,
        RealmCalendar calendar,
        DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        // FINALISE2: the gate's schedule mode (always / closed / custom windows) first; "compiled" = the code below.
        return Godswar.Server.Application.Gates.GateRules.IsOpen(
            GateKey(destination),
            calendar.ToRealmTime(instant).DateTime,
            () => CompiledIsOpen(destination, calendar, instant));
    }

    private static bool CompiledIsOpen(
        BattlefieldDestinationKind destination,
        RealmCalendar calendar,
        DateTimeOffset instant)
    {
        if (destination is BattlefieldDestinationKind.DuelArena)
        {
            // The Duel Arena is always available.
            return true;
        }

        if (destination is BattlefieldDestinationKind.LelantineFarm)
        {
            // FINALISE (candidate merge of finalise-server 6cb15ba + finalise-pets 4acf8f2), in this order:
            // 1. event.lelantine_always_open = 1 restores the old always-open test behaviour.
            // 2. event_schedule_overrides rows for activity "LelantineFarm" (editor) replace the default window
            //    (e.g. day_of_week 5, start 1305, 45 min = Friday 21:45-22:30). Round scoring keeps its own clock.
            // 3. Default (the admin 2026-11-05: "open only at its event times"): the farm opens only for its round,
            //    Saturday 22:45-23:30 (IGG guide acid=185, the GodsArena wiki and the client's own NF_L0_Y7004
            //    line agree). The window is the round clock's own (LelantineFarmRoundPolicy, Asia/Manila).
            if (ServerRates.Current.Absolute(ServerRateKeys.LelantineAlwaysOpen) == 1)
            {
                return true;
            }

            // FINALISE2 (the admin F-03/F-04): the round window in REALM time (AEST on live), not a fixed Manila clock.
            // Editor rows were applied by GateRules.IsOpen above (rows alone switch the farm's windows, as before).
            return LelantineFarmRoundPolicy.IsOpen(instant, calendar);
        }

        // ROUND2: always-open switches, window length and a start-time shift
        // from server_rates (stock: windows below, 45 minutes, no shift).
        var rates = ServerRates.Current;
        if (destination == BattlefieldDestinationKind.Pindus &&
                rates.Absolute(ServerRateKeys.PindusAlwaysOpen) == 1 ||
            destination is BattlefieldDestinationKind.NiMiniLower or
                BattlefieldDestinationKind.NiMiniUpper &&
                rates.Absolute(ServerRateKeys.NiMiniAlwaysOpen) == 1)
        {
            return true;
        }

        // Editor-written windows replace the compiled ones and carry their own length; the rates' length and shift do not apply.
        var activity = destination == BattlefieldDestinationKind.Pindus ? EventContent.Pindus : EventContent.NiMini;
        if (NpcContentOverrides.Current.Events.Windows.TryGetValue(activity, out var edited))
        {
            return EventSchedulePolicy.IsOpen(edited, calendar.ToRealmTime(instant).DateTime);
        }

        var duration = TimeSpan.FromMinutes(
            rates.Absolute(ServerRateKeys.BattlefieldWindowMinutes));
        var shift = TimeSpan.FromHours(
            rates.Absolute(ServerRateKeys.BattlefieldShiftHours));
        if (duration == EventDuration && shift == TimeSpan.Zero)
        {
            var local = calendar.ToRealmTime(instant);
            var time = TimeOnly.FromDateTime(local.DateTime);
            return Starts(destination, local.DayOfWeek)
                .Any(start => IsWithin(time, start, EventDuration));
        }

        // A shifted or longer window may cross midnight: test the windows of
        // the previous, current and next day as absolute instants.
        var realmNow = calendar.ToRealmTime(instant).DateTime;
        for (var dayOffset = -1; dayOffset <= 1; dayOffset++)
        {
            var day = realmNow.Date.AddDays(dayOffset);
            foreach (var start in Starts(destination, day.DayOfWeek))
            {
                var opens = day + start.ToTimeSpan() + shift;
                if (realmNow >= opens && realmNow < opens + duration)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static IEnumerable<object> DescribeCompiled(BattlefieldDestinationKind destination) =>
        Enum.GetValues<DayOfWeek>().SelectMany(day => Starts(destination, day).Select(start => (object)new
        {
            day = (int)day,
            start_minute = start.Hour * 60 + start.Minute,
            duration_minutes = (int)EventDuration.TotalMinutes
        }));

    // RELEASE: internal so the event board (EVENTSBOARD) shows the same windows, IGG switches included.
    internal static IEnumerable<TimeOnly> Starts(
        BattlefieldDestinationKind destination,
        DayOfWeek day) =>
        destination switch
        {
            // BATTLEFIELD2: the IGG guide's Pindus times (acid=123: Wednesday 11:00-11:45, Saturday 21:00-21:45,
            // Sunday 11:00-11:45). The guide says "server time: GMT-5"; they are read as realm time, like Ni Mini,
            // and the client's own transporter line NF_L0_Y7002 prints the same clock times with no zone.
            // battlefield.pindus_igg_times = 0 restores the stock windows below.
            BattlefieldDestinationKind.Pindus when
                ServerRates.Current.Absolute(ServerRateKeys.BattlefieldPindusIggTimes) == 1 => day switch
            {
                DayOfWeek.Wednesday => [new TimeOnly(11, 0)],
                DayOfWeek.Saturday => [new TimeOnly(21, 0)],
                DayOfWeek.Sunday => [new TimeOnly(11, 0)],
                _ => []
            },
            BattlefieldDestinationKind.Pindus => day switch
            {
                DayOfWeek.Wednesday => [new TimeOnly(21, 0)],
                DayOfWeek.Friday => [new TimeOnly(21, 0)],
                DayOfWeek.Saturday =>
                    [new TimeOnly(8, 0), new TimeOnly(15, 0)],
                DayOfWeek.Sunday =>
                    [new TimeOnly(8, 0), new TimeOnly(21, 0)],
                _ => []
            },
            // BATTLEFIELD: the IGG guide's Ni Mini times (acid=182: Fridays 21:45-22:30,
            // Sundays 19:00-19:45; read as realm time, the guide's own zone is not stated
            // for Ni Mini). battlefield.nimini_igg_times = 0 restores the stock windows.
            BattlefieldDestinationKind.NiMiniLower or
            BattlefieldDestinationKind.NiMiniUpper when
                ServerRates.Current.Absolute(ServerRateKeys.BattlefieldNiMiniIggTimes) == 1 => day switch
            {
                DayOfWeek.Friday => [new TimeOnly(21, 45)],
                DayOfWeek.Sunday => [new TimeOnly(19, 0)],
                _ => []
            },
            BattlefieldDestinationKind.NiMiniLower or
            BattlefieldDestinationKind.NiMiniUpper => day switch
            {
                DayOfWeek.Friday => [new TimeOnly(19, 0)],
                DayOfWeek.Saturday => [new TimeOnly(7, 0)],
                DayOfWeek.Sunday => [new TimeOnly(19, 0)],
                _ => []
            },
            _ => []
        };

    /// <summary>
    /// BATTLEFIELD2: true when Pindus runs exactly the IGG windows the client's NF_L0_Y7002 line prints (IGG times on,
    /// no editor window, stock 45-minute length and no shift), so the transporter may answer "closed" with that line.
    /// </summary>
    public static bool PindusMatchesIggText()
    {
        var rates = ServerRates.Current;
        return rates.Absolute(ServerRateKeys.BattlefieldPindusIggTimes) == 1 &&
               rates.Absolute(ServerRateKeys.BattlefieldWindowMinutes) == (long)EventDuration.TotalMinutes &&
               rates.Absolute(ServerRateKeys.BattlefieldShiftHours) == 0 &&
               !NpcContentOverrides.Current.Events.Windows.ContainsKey(EventContent.Pindus);
    }

    private static bool IsWithin(
        TimeOnly current,
        TimeOnly start,
        TimeSpan duration)
    {
        var elapsed = current.ToTimeSpan() - start.ToTimeSpan();
        return elapsed >= TimeSpan.Zero && elapsed < duration;
    }
}
