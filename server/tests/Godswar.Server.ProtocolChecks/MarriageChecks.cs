using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Rates;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Characters;

namespace Godswar.Server.ProtocolChecks;

// MARRIAGE: Cupid / Cupid's Shadow rules, the click reader, the booking clock, the fines and the switch.
internal static class MarriageChecks
{
    public const string CheckName = "Marriage reads Cupid clicks, books whole hours, prices fines and is off by default";

    public static Task RunAsync()
    {
        // Click paths: the guild-altar layout (first click at +12) and the Class Shifter capture (+12 = -1).
        var altar = MarriagePolicy.ResolveClick([1, 14, -1, -1, -1]);
        Check.Equal(14, altar.Clicked, "altar layout: clicked");
        Check.Equal(1, altar.PageOneEntry(1, 2, 3), "altar layout: page-one entry");
        var shifter = MarriagePolicy.ResolveClick([-1, 3, 14, -1, -1]);
        Check.Equal(14, shifter.Clicked, "shifter layout: clicked");
        Check.Equal(3, shifter.PageOneEntry(1, 2, 3), "shifter layout: page-one entry");
        var submit = MarriagePolicy.ResolveClick([2, 0, -1, -1, -1]);
        Check.Equal(2, submit.Clicked, "form submission keeps the entry that opened it");
        Check.Equal(1, submit.TrailingZeros, "form submission has one confirm key");
        Check.Equal(2, MarriagePolicy.ResolveClick([2, 0, 0, -1, -1]).TrailingZeros, "companion packet has two");
        Check.Equal(0, MarriagePolicy.ResolveClick([-1, -1, -1, -1, -1]).Path.Length, "opening request has no path");
        Check.Equal(1, MarriagePolicy.ResolveClick([-1, 1, -1, -1, -1]).Clicked, "page-one click in +16");

        // Booking boxes: weekday in +0x38, hour in +0x3C (UNPROVEN), or weekday*100+hour in the first box.
        Check.True(MarriagePolicy.TryReadBookingRequest(5, 14, out var day, out var hour) && day == 5 && hour == 14, "two boxes");
        Check.True(MarriagePolicy.TryReadBookingRequest(7, 0, out day, out hour) && day == 7 && hour == 0, "hour 0 is valid");
        Check.True(MarriagePolicy.TryReadBookingRequest(514, -1, out day, out hour) && day == 5 && hour == 14, "514 = Fri 14:00");
        Check.True(!MarriagePolicy.TryReadBookingRequest(8, 3, out _, out _), "weekday 8 refused");
        Check.True(!MarriagePolicy.TryReadBookingRequest(3, 24, out _, out _), "hour 24 refused");
        Check.True(!MarriagePolicy.TryReadBookingRequest(-1, -1, out _, out _), "empty boxes refused");
        Check.True(!MarriagePolicy.TryReadBookingRequest(160, -1, out _, out _), "160 is not an hour");

        // Monday 2026-11-02 10:20 realm time (+10:00).
        var now = new DateTimeOffset(2026, 11, 2, 10, 20, 0, TimeSpan.FromHours(10));
        Check.True(!MarriagePolicy.TryResolveSlot(now, 1, 10, out _), "the current hour is refused");
        Check.True(MarriagePolicy.TryResolveSlot(now, 1, 11, out var slot) &&
                   slot == new DateTimeOffset(2026, 11, 2, 11, 0, 0, now.Offset), "later today");
        Check.True(MarriagePolicy.TryResolveSlot(now, 1, 9, out slot) &&
                   slot == new DateTimeOffset(2026, 11, 9, 9, 0, 0, now.Offset), "earlier hour today wraps a week");
        Check.True(MarriagePolicy.TryResolveSlot(now, 7, 23, out slot) &&
                   slot == new DateTimeOffset(2026, 11, 8, 23, 0, 0, now.Offset), "Sunday is day 7");
        Check.Equal(110809, MarriagePolicy.EncodeSlot(new DateTimeOffset(2026, 11, 8, 9, 0, 0, now.Offset)), "month*10000+day*100+hour");

        var start = new DateTimeOffset(2026, 11, 3, 1, 0, 0, TimeSpan.Zero);
        Check.True(MarriagePolicy.SlotState(null, start) == MarriageSlotState.None, "no slot");
        Check.True(MarriagePolicy.SlotState(start, start.AddMinutes(-1)) == MarriageSlotState.Waiting, "before the hour");
        Check.True(MarriagePolicy.SlotState(start, start.AddMinutes(59)) == MarriageSlotState.Open, "inside the hour");
        Check.True(MarriagePolicy.SlotState(start, start.AddHours(1)) == MarriageSlotState.Expired, "after the hour");

        // Fines (client text AWAY1001/1002, wiki-56).
        Check.True(MarriagePolicy.ForcedFine(true, 0, 0) == new MarriageFine(0, 0), "absent partner: free");
        Check.True(MarriagePolicy.ForcedFine(false, 200, 0) == new MarriageFine(200, 0), "200 Rep first");
        Check.True(MarriagePolicy.ForcedFine(false, 199, 100_000) == new MarriageFine(0, 100_000), "else 100,000 Silver");
        Check.True(MarriagePolicy.ForcedFine(false, 199, 99_999) is null, "neither: refused");
        var t = DateTimeOffset.UtcNow;
        Check.True(MarriagePolicy.IsAbsent(t.AddDays(-15), false, t), "15 days away");
        Check.True(!MarriagePolicy.IsAbsent(t.AddDays(-14.9), false, t), "14.9 days is not");
        Check.True(!MarriagePolicy.IsAbsent(t.AddDays(-30), true, t), "online now is not absent");

        // Review fix 1: a deleted partner (LastOnlineSql yields NULL) is absent; the account's timestamps are
        // not read; deleting a character ends its couple and strips the marriage titles; purge keeps history.
        Check.True(MarriagePolicy.IsAbsent(null, false, t), "deleted/unknown partner (NULL last-online) is absent");
        var lastOnline = PostgresMarriageStore.LastOnlineSql;
        Check.True(!lastOnline.Contains("accounts", StringComparison.OrdinalIgnoreCase) &&
                   !lastOnline.Contains("last_logout_time", StringComparison.Ordinal),
            "15-day rule ignores account-level timestamps");
        Check.True(lastOnline.Contains("lifecycle_state = 'active'", StringComparison.Ordinal),
            "15-day rule treats a non-active character as absent");
        var schema = PostgresMarriageStore.SchemaSql;
        Check.True(schema.Contains("AFTER UPDATE OF lifecycle_state ON public.character_base", StringComparison.Ordinal) &&
                   schema.Contains("BEFORE DELETE ON public.character_base", StringComparison.Ordinal),
            "tombstone and hard-delete triggers end the couple");
        Check.True(schema.Contains("title_id IN (1401, 1402) AND granted_by = 'marriage'", StringComparison.Ordinal),
            "deletion strips only the marriage titles");
        Check.True(!schema.Contains("ON DELETE CASCADE", StringComparison.Ordinal) &&
                   schema.Contains("ON DELETE SET NULL", StringComparison.Ordinal),
            "a purge keeps the couple history (SET NULL, not CASCADE)");

        // Switch and prices.
        var defaults = ServerRatesSnapshot.Default;
        Check.True(!MarriagePolicy.IsEnabled(null, defaults), "off by default");
        Check.True(MarriagePolicy.IsEnabled("true", defaults) && MarriagePolicy.IsEnabled("1", defaults), "env turns it on");
        var on = ServerRatesSnapshot.Create([new(ServerRateKeys.MarriageEnabled, 1m)], 1, DateTimeOffset.UtcNow, "check", "test");
        Check.True(MarriagePolicy.IsEnabled(null, on), "rate npc.marriage = 1 turns it on");
        Check.Equal(9999, MarriagePolicy.EngagementSilver(defaults), "retail engagement 9999 Silver");
        Check.Equal(100, MarriagePolicy.BookingGold(defaults), "retail booking 100 Gold");
        Check.True(MarriagePolicy.NecklaceRequired(defaults), "necklace required by default");
        Check.Equal(0m, ServerRateCatalog.Get(ServerRateKeys.MarriageEnabled).DefaultValue, "switch defaults to 0");

        // Regression: the old Cupid / Shadow tables are what answers while the switch is off.
        Check.True(OpeningOf("CupidDialogue").SequenceEqual([6, 1, 2, 3, 4, 5, 7]), "Cupid opening: stock entries plus 7 (F-130 necklace)");
        Check.True(OpeningOf("CupidsShadowDialogue").SequenceEqual([1, 2]), "Shadow opening unchanged");
        Check.True(PostgresMarriageStore.SchemaSql.Contains("CREATE TABLE IF NOT EXISTS public.character_marriages",
            StringComparison.Ordinal), "first-use DDL is idempotent");
        return Task.CompletedTask;
    }

    private static IReadOnlyList<int> OpeningOf(string field)
    {
        var value = typeof(GameClientHandler).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return ((GameClientHandler.ScriptedNpcDialogue)value).OpeningMenu;
    }
}
