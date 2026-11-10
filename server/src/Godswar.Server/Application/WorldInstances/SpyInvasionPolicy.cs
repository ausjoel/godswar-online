using Godswar.Server.Application.Events;
using Godswar.Server.Application.Gates; // FINALISE2
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms; // FINALISE2

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// The capital-city spies (Spartan spies in Athens, Athenian spies in Sparta)
/// exist only while an invasion runs: Monday, Wednesday and Saturday at 19:00
/// realm time for 30 minutes. A wave arrives at 19:00 and
/// every 5 minutes after it (19:00, :05, :10, :15, :20, :25): a spy that was
/// killed returns with the next wave, survivors leave when the window closes.
/// Off unless <c>event.spy_invasion</c> is 1 (spies then stay permanent).
/// FINALISE2 (the admin F-04): realm time = AEST on live (was a fixed UTC+8 clock). The windows are the gate
/// <c>event.spy_invasion</c> (editor: always / closed / custom windows; a wave every 5 minutes inside each window).
/// </summary>
internal static class SpyInvasionPolicy
{
    private static readonly TimeSpan Slot = TimeSpan.FromMinutes(5);

    // Map 0 (Sparta) holds the Athenian spies, map 1 (Athens) the Spartan ones.
    private static readonly HashSet<string> TemplateKeys = new(StringComparer.Ordinal)
    {
        "A_normals_greecewarrior_002", "A_normals_greecewarrior_004", "A_normals_greecewarrior_012",
        "A_normalsA_greecewarrior_002", "A_normalsB_greecewarrior_002",
        "A_normals_greecewarrior_001", "A_normals_greecewarrior_003", "A_normals_greecewarrior_008",
        "A_normalsA_greecewarrior_001", "A_normalsB_greecewarrior_001"
    };

    public static bool IsSpy(short mapId, string? templateKey) =>
        mapId is 0 or 1 && templateKey is not null && TemplateKeys.Contains(templateKey);

    public static bool Enabled =>
        ServerRates.Current.Absolute(ServerRateKeys.SpyInvasion) == 1;

    private static DateTime RealmNow(DateTimeOffset instant, RealmCalendar? calendar) => // FINALISE2
        (calendar ?? RealmClock.Calendar)?.ToRealmTime(instant).DateTime ?? instant.UtcDateTime;

    private static DateTimeOffset ToUtc(DateTime local, RealmCalendar? calendar) => // FINALISE2
        (calendar ?? RealmClock.Calendar)?.FromRealmWallClock(local) ??
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero);

    /// <summary>The windows in realm wall-clock time (compiled Mon/Wed/Sat 19:00 for 30 minutes, or the editor's).</summary>
    public static IReadOnlyList<EventWindow> Windows => GateRules.RoundWindows(GateKeys.Spies);

    /// <summary>True while a window is open (start-inclusive, end-exclusive).</summary>
    public static bool IsActive(DateTimeOffset instant, RealmCalendar? calendar = null) // FINALISE2
    {
        var realm = RealmNow(instant, calendar);
        return GateRules.IsOpen(GateKeys.Spies, realm, () => EventSchedulePolicy.IsOpen(Windows, realm));
    }

    /// <summary>The first wave boundary strictly after <paramref name="instant"/>.</summary>
    public static DateTimeOffset NextWaveStart(DateTimeOffset instant, RealmCalendar? calendar = null) // FINALISE2
    {
        switch (GateRules.Mode(GateKeys.Spies)) // FINALISE2
        {
            case GateSchedule.Always:
                return instant + Slot;
        }

        var realm = RealmNow(instant, calendar);
        var windows = GateRules.Mode(GateKeys.Spies) == GateSchedule.Closed ? [] : Windows;
        DateTimeOffset? best = null;
        for (var ahead = -1; ahead <= 8; ahead++)
        {
            var day = realm.Date.AddDays(ahead); // FINALISE2
            foreach (var window in windows)
            {
                if (window.Day == EventContent.ClosedDay || (int)day.DayOfWeek != window.Day) // FINALISE2
                {
                    continue;
                }

                var opens = day.AddMinutes(window.StartMinute); // FINALISE2
                for (var slot = 0; slot * Slot.TotalMinutes < window.DurationMinutes; slot++)
                {
                    var wave = ToUtc(opens.AddMinutes(slot * Slot.TotalMinutes), calendar); // FINALISE2
                    if (wave > instant && (best is null || wave < best))
                    {
                        best = wave;
                    }
                }
            }
        }

        // Closed (or no window within the week): look again in a day, like a far-away wave.
        return best ?? instant.AddDays(1); // FINALISE2
    }
}
