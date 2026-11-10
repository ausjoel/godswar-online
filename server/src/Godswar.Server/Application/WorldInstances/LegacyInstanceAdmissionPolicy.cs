using Godswar.Server.Application.Npc; // FINALISE2
using Godswar.Server.Application.Rates; // ROUND2
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

internal enum LegacyInstanceScheduleStatus : byte
{
    Open = 1,
    WrongDay = 2,
    DailyCutoffPassed = 3,

    /// <summary>FINALISE2: the gate is closed by the editor (schedule mode "closed" or outside custom windows).</summary>
    Closed = 4
}

internal static class LegacyInstanceAdmissionPolicy
{
    private static readonly TimeOnly WonderlandCutoff =
        new(23, 0);

    /// <summary>FINALISE2: the gate key of each Instance Caller entry (Gates table).</summary>
    public static string GateKey(InstanceCallerEntryKind kind) => kind switch
    {
        InstanceCallerEntryKind.Atlantis => LevelBandGates.Atlantis,
        InstanceCallerEntryKind.Wonderland => LevelBandGates.Wonderland,
        InstanceCallerEntryKind.HarborAttack => LevelBandGates.HarborAttack,
        InstanceCallerEntryKind.TrainingGround => LevelBandGates.TrainingGround,
        _ when WarriorTrainingGroundPolicy.TryGetByKind(kind, out var warrior) => warrior.GateKey, // WARRIOR (F-51)
        _ => "instance.other"
    };

    public static LegacyInstanceScheduleStatus CheckSchedule(
        InstanceCallerEntryKind kind,
        RealmCalendar calendar,
        DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        // FINALISE2 (the admin F-64): every instance's opening mode is editable (always / closed / custom windows);
        // "compiled" keeps the code below (Wonderland's daily cut-off, the others always open).
        var key = GateKey(kind);
        var realmNow = calendar.ToRealmTime(instant).DateTime;
        if (Godswar.Server.Application.Gates.GateRules.Mode(key) != Godswar.Server.Application.Gates.GateSchedule.Compiled ||
            Godswar.Server.Application.Gates.GateRules.EditedWindows(key) is not null)
        {
            return Godswar.Server.Application.Gates.GateRules.IsOpen(key, realmNow, static () => true)
                ? LegacyInstanceScheduleStatus.Open
                : kind == InstanceCallerEntryKind.Wonderland
                    ? LegacyInstanceScheduleStatus.DailyCutoffPassed
                    : LegacyInstanceScheduleStatus.Closed;
        }

        return CompiledSchedule(kind, calendar, instant);
    }

    private static LegacyInstanceScheduleStatus CompiledSchedule(
        InstanceCallerEntryKind kind,
        RealmCalendar calendar,
        DateTimeOffset instant)
    {
        if (kind != InstanceCallerEntryKind.Wonderland)
        {
            return LegacyInstanceScheduleStatus.Open;
        }

        var local = calendar.ToRealmTime(instant);

        // The reference only opened 飘渺幻境 on Saturday and Sunday. This server
        // runs it every day by the operator's decision (2026-09-28), so only the
        // daily cut-off remains - the part the client's own text states: the
        // instance is free to enter before 23:00 server time (Asia/Manila).
        // ROUND2: event.wonderland_close_hour (stock 23; 24 = no cut-off).
        var closeHour = ServerRates.Current.Absolute(
            ServerRateKeys.WonderlandCloseHour);
        if (closeHour >= 24)
        {
            return LegacyInstanceScheduleStatus.Open;
        }

        var cutoff = closeHour == WonderlandCutoff.Hour
            ? WonderlandCutoff
            : new TimeOnly((int)closeHour, 0);
        return TimeOnly.FromDateTime(local.DateTime) < cutoff
            ? LegacyInstanceScheduleStatus.Open
            : LegacyInstanceScheduleStatus.DailyCutoffPassed;
    }
}
