using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static class BattlefieldSchedulePolicyChecks
{
    public const string CheckName =
        "Realm-calendar Battlefield admission windows";

    private static readonly RealmCalendar Manila =
        RealmCalendar.CreateForTesting(RealmId.Tempest, "Asia/Manila");

    public static Task RunAsync()
    {
        CheckPindusWindows();
        CheckNiMiniWindows();
        CheckDuelAndUnknownPolicies();
        return Task.CompletedTask;
    }

    private static void CheckPindusWindows()
    {
        // BATTLEFIELD2: by default Pindus follows the IGG guide (Wed 11:00, Sat 21:00, Sun 11:00);
        // battlefield.pindus_igg_times = 0 keeps the stock windows checked below.
        DateTimeOffset[] igg =
        [
            ManilaTime(2026, 9, 2, 11, 0),
            ManilaTime(2026, 9, 5, 21, 0),
            ManilaTime(2026, 9, 6, 11, 0)
        ];
        CheckWindows(BattlefieldDestinationKind.Pindus, igg);
        Check.True(
            !IsOpen(BattlefieldDestinationKind.Pindus, ManilaTime(2026, 9, 2, 21, 0)) &&
            !IsOpen(BattlefieldDestinationKind.Pindus, ManilaTime(2026, 9, 4, 21, 0)) &&
            !IsOpen(BattlefieldDestinationKind.Pindus, ManilaTime(2026, 9, 5, 8, 0)) &&
            !IsOpen(BattlefieldDestinationKind.Pindus, ManilaTime(2026, 9, 6, 21, 0)) &&
            !IsOpen(BattlefieldDestinationKind.Pindus, ManilaTime(2026, 9, 3, 11, 0)) &&
            BattlefieldSchedulePolicy.PindusMatchesIggText(),
            "Pindus is closed at the stock starts by default and matches the client's IGG times line");
        Godswar.Server.Application.Rates.ServerRates.Install(
            Godswar.Server.Application.Rates.ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Godswar.Server.Application.Rates.ServerRateKeys.BattlefieldPindusIggTimes, 0m)],
                1, DateTimeOffset.UtcNow, "check", "test"));
        try
        {
            CheckStockPindusWindows();
            Check.True(!BattlefieldSchedulePolicy.PindusMatchesIggText(),
                "with the IGG times off the transporter does not print the IGG times line");
        }
        finally
        {
            Godswar.Server.Application.Rates.ServerRates.ResetToDefaults();
        }
        Godswar.Server.Application.Rates.ServerRates.Install(
            Godswar.Server.Application.Rates.ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Godswar.Server.Application.Rates.ServerRateKeys.BattlefieldShiftHours, 2m)],
                1, DateTimeOffset.UtcNow, "check", "test"));
        try
        {
            Check.True(!BattlefieldSchedulePolicy.PindusMatchesIggText() &&
                       IsOpen(BattlefieldDestinationKind.Pindus, ManilaTime(2026, 9, 2, 13, 0)) &&
                       !IsOpen(BattlefieldDestinationKind.Pindus, ManilaTime(2026, 9, 2, 11, 0)),
                "a shifted IGG window moves and no longer claims the client's IGG times line");
        }
        finally
        {
            Godswar.Server.Application.Rates.ServerRates.ResetToDefaults();
        }
    }

    private static void CheckStockPindusWindows()
    {
        DateTimeOffset[] starts =
        [
            ManilaTime(2026, 9, 2, 21, 0),
            ManilaTime(2026, 9, 4, 21, 0),
            ManilaTime(2026, 9, 5, 8, 0),
            ManilaTime(2026, 9, 5, 15, 0),
            ManilaTime(2026, 9, 6, 8, 0),
            ManilaTime(2026, 9, 6, 21, 0)
        ];
        CheckWindows(BattlefieldDestinationKind.Pindus, starts);
        Check.True(
            !IsOpen(
                BattlefieldDestinationKind.Pindus,
                ManilaTime(2026, 9, 1, 21, 0)) &&
            !IsOpen(
                BattlefieldDestinationKind.Pindus,
                ManilaTime(2026, 9, 3, 21, 0)) &&
            !IsOpen(
                BattlefieldDestinationKind.Pindus,
                ManilaTime(2026, 9, 5, 9, 0)) &&
            !IsOpen(
                BattlefieldDestinationKind.Pindus,
                ManilaTime(2026, 9, 6, 9, 0)),
            "Pindus stays closed outside its advertised realm windows");
    }

    private static void CheckNiMiniWindows()
    {
        // RELEASE: battlefield.nimini_igg_times now defaults to 0 (stock windows); the release default is
        // checked first, then the switch is turned on for the IGG part (Fri 21:45, Sun 19:00).
        Godswar.Server.Application.Rates.ServerRates.ResetToDefaults();
        Check.True(IsOpen(BattlefieldDestinationKind.NiMiniUpper, ManilaTime(2026, 9, 4, 19, 0)),
            "RELEASE default: Ni Mini opens at the stock Friday 19:00 (nimini_igg_times = 0)");
        Godswar.Server.Application.Rates.ServerRates.Install(
            Godswar.Server.Application.Rates.ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Godswar.Server.Application.Rates.ServerRateKeys.BattlefieldNiMiniIggTimes, 1m)],
                1, DateTimeOffset.UtcNow, "check", "test"));
        DateTimeOffset[] igg =
        [
            ManilaTime(2026, 9, 4, 21, 45),
            ManilaTime(2026, 9, 6, 19, 0)
        ];
        CheckWindows(BattlefieldDestinationKind.NiMiniLower, igg);
        CheckWindows(BattlefieldDestinationKind.NiMiniUpper, igg);
        Check.True(
            !IsOpen(BattlefieldDestinationKind.NiMiniUpper, ManilaTime(2026, 9, 4, 19, 0)) &&
            !IsOpen(BattlefieldDestinationKind.NiMiniUpper, ManilaTime(2026, 9, 5, 7, 0)),
            "Ni Mini is closed at the stock Friday 19:00 and Saturday 07:00 starts with the IGG switch on");
        Godswar.Server.Application.Rates.ServerRates.Install(
            Godswar.Server.Application.Rates.ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Godswar.Server.Application.Rates.ServerRateKeys.BattlefieldNiMiniIggTimes, 0m)],
                1, DateTimeOffset.UtcNow, "check", "test"));
        try
        {
            DateTimeOffset[] starts =
            [
                ManilaTime(2026, 9, 4, 19, 0),
                ManilaTime(2026, 9, 5, 7, 0),
                ManilaTime(2026, 9, 6, 19, 0)
            ];
            CheckWindows(BattlefieldDestinationKind.NiMiniLower, starts);
            CheckWindows(BattlefieldDestinationKind.NiMiniUpper, starts);
            Check.True(
                !IsOpen(
                    BattlefieldDestinationKind.NiMiniUpper,
                    ManilaTime(2026, 9, 2, 19, 0)) &&
                !IsOpen(
                    BattlefieldDestinationKind.NiMiniUpper,
                    ManilaTime(2026, 9, 5, 8, 0)) &&
                !IsOpen(
                    BattlefieldDestinationKind.NiMiniUpper,
                    ManilaTime(2026, 9, 6, 20, 0)),
                "Ni Mini stays closed outside Friday, Saturday, and Sunday windows");
        }
        finally
        {
            // BATTLEFIELD: never leak the rate into later checks, even when a check above throws.
            Godswar.Server.Application.Rates.ServerRates.ResetToDefaults();
        }
    }

    private static void CheckDuelAndUnknownPolicies()
    {
        Check.True(
            IsOpen(
                BattlefieldDestinationKind.DuelArena,
                ManilaTime(2026, 9, 1, 0, 0)) &&
            IsOpen(
                BattlefieldDestinationKind.DuelArena,
                ManilaTime(2026, 9, 6, 23, 59)) &&
            !IsOpen(
                (BattlefieldDestinationKind)byte.MaxValue,
                ManilaTime(2026, 9, 4, 19, 0)),
            "Duel is always open while unknown destinations fail closed");
        Check.Throws<ArgumentNullException>(
            () => BattlefieldSchedulePolicy.IsOpen(
                BattlefieldDestinationKind.Pindus,
                null!,
                ManilaTime(2026, 9, 2, 21, 0)),
            "schedule evaluation requires a realm calendar");
    }

    private static void CheckWindows(
        BattlefieldDestinationKind destination,
        IEnumerable<DateTimeOffset> starts)
    {
        foreach (var start in starts)
        {
            var end = start.AddMinutes(45);
            Check.True(
                !IsOpen(destination, start.AddTicks(-1)) &&
                IsOpen(destination, start) &&
                IsOpen(destination, end.AddTicks(-1)) &&
                !IsOpen(destination, end),
                $"{destination} uses a start-inclusive 45-minute realm window at {start:O}");
        }
    }

    private static bool IsOpen(
        BattlefieldDestinationKind destination,
        DateTimeOffset instant) =>
        BattlefieldSchedulePolicy.IsOpen(destination, Manila, instant);

    private static DateTimeOffset ManilaTime(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(
            year,
            month,
            day,
            hour,
            minute,
            second: 0,
            TimeSpan.FromHours(8));
}
