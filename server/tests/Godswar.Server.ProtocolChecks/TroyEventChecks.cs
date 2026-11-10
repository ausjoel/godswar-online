using System.Collections;
using System.Reflection;
using Godswar.Server.Application.Gates;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Troy;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W2 F-27 TROY: the Trojan Expedition's encodings, window, switches and routing.</summary>
internal static class TroyEventChecks
{
    public const string CheckName = "W2 F-27 Troy event: encodings, Thursday window, switches, awarder routing";

    public static Task RunAsync()
    {
        // The client's own encodings (NpcFunTroy.lua).
        Check.Equal(37003, TroyEvent.RankingLine(37, 3), "rank 3 with 37 points = 37003");
        Check.Equal(37011, TroyEvent.RankingLine(37, 11), "guild score line = score*1000 + 11");
        Check.Equal(12, TroyEvent.RankingLine(0, 12), "a zero own score is the bare slot 12, never a multiple of 1000");
        Seq(new[] { 311 }, TroyEvent.RankingPage([], null, 0), "nothing scored = 'No rankings'");
        Seq(new[] { 50001, 37002, 37011, 9012 }, TroyEvent.RankingPage([50, 37], 37, 9),
            "two guilds, own guild second, own score 9");
        Check.True(TroyEvent.RankingPage(Enumerable.Range(1, 15).Select(static i => (long)i).ToList(), null, 1).Length == 11,
            "at most ten guild lines");

        using (Rates((TroyEvent.RewardPercent, 100m)))
        {
            var silver = TroyEvent.GuildSilver(37);
            Check.Equal(37_000L, silver, "guild Silver = score x 1,000 (IGG)");
            Check.Equal(370L, TroyEvent.GuildGold(silver), "guild Gold = score x 10 = Silver / 100 (the client's own print)");
            Check.Equal(37_013, TroyEvent.GuildRewardLine(silver), "guild reward line = Silver + 13");
            Check.Equal(37, TroyEvent.ExpPills(37), "1 point = 1 Exp Pill");
            Check.Equal(37_016, TroyEvent.PersonalRewardLine(37), "personal reward line = pills*1000 + 16");
            Check.True(TroyEvent.GuildRewardLine(TroyEvent.GuildSilver(10_000_000)) % 1000 == 13 &&
                       TroyEvent.PersonalRewardLine(TroyEvent.ExpPills(10_000_000)) % 1000 == 16,
                "huge scores are clamped and keep their tails (no int overflow)");
        }

        using (Rates((TroyEvent.RewardPercent, 50m)))
        {
            Check.Equal(18, TroyEvent.ExpPills(37), "reward size 50 % halves the pills");
        }

        Check.Equal(1, TroyEvent.PointsFor(false, false), "normal kill 1 point (IGG)");
        Check.Equal(5, TroyEvent.PointsFor(true, false), "elite kill 5 points (IGG)");
        Check.Equal(10, TroyEvent.PointsFor(false, true), "boss kill 10 points (IGG)");

        // Window: Thursday 20:00-21:00 realm time (NF_L0_TR2801), the gate catalogue's compiled windows.
        var gate = GateCatalog.Find(TroyEvent.GateKey);
        Check.True(gate is { CompiledMinimum: 50, CompiledMaximum: GateCatalog.LevelCap }, "event.troy gate: Lv50-200");
        var thursday = new DateTime(2026, 11, 12); // a Thursday
        Check.True(thursday.DayOfWeek == DayOfWeek.Thursday, "test date is a Thursday");
        bool Open(DateTime at) => GateRules.IsOpen(TroyEvent.GateKey, at, () => GateRules.CompiledWindowsOpen(TroyEvent.GateKey, at));
        Check.True(!Open(thursday.AddHours(19).AddMinutes(59)), "closed Thu 19:59");
        Check.True(Open(thursday.AddHours(20)), "open Thu 20:00");
        Check.True(Open(thursday.AddHours(20).AddMinutes(59)), "open Thu 20:59");
        Check.True(!Open(thursday.AddHours(21)), "closed Thu 21:00");
        Check.True(!Open(thursday.AddDays(1).AddHours(20).AddMinutes(30)), "closed Fri 20:30");

        var windows = TroyEvent.CompiledWindows;
        var day = DateOnly.FromDateTime(thursday);
        Check.Equal(day, TroyEvent.ScoringDay(windows, thursday.AddHours(20).AddMinutes(30)), "kills score for the Thursday");
        Check.True(TroyEvent.ClaimDay(windows, thursday.AddHours(20).AddMinutes(30), false) is null, "no claims while running");
        Same(day, TroyEvent.ClaimDay(windows, thursday.AddHours(21), false), "claims open at 21:00");
        Same(day, TroyEvent.ClaimDay(windows, thursday.AddHours(23).AddMinutes(54), false), "claims open at 23:54");
        Check.True(TroyEvent.ClaimDay(windows, thursday.AddHours(23).AddMinutes(55), false) is null, "claims closed at 23:55");
        Check.True(TroyEvent.ClaimDay(windows, thursday.AddDays(1).AddHours(12), false) is null, "claims closed next day");
        Same(DateOnly.FromDateTime(thursday.AddDays(2)),
            TroyEvent.ClaimDay(windows, thursday.AddDays(2).AddHours(9), true), "gate forced always: today's scores claimable");

        // Switch defaults: coordinator and awards on (F-27 build all, do not hide), entry off until Troy has monsters.
        var defaults = TroyEvent.Definitions.ToDictionary(static d => d.Key, static d => d.DefaultValue);
        Check.True(defaults[TroyEvent.CoordinatorSwitch] == 1m && defaults[TroyEvent.AwardsSwitch] == 1m &&
                   defaults[TroyEvent.EntrySwitch] == 0m, "defaults: coordinator 1, awards 1, entry 0");

        // Routing: the Event Transporter keeps the captured menu with entry off and gains 900 with it on; Athens_124 is
        // routed to the awarder only while troy.awards is on.
        using (Rates((TroyEvent.EntrySwitch, 0m), (TroyEvent.AwardsSwitch, 0m)))
        {
            Seq(new[] { 200, 700, 501, 600, 202 }, OpeningMenu("Sparta_072", 1), "entry off: captured transporter menu");
            Check.True(Functions("Athens_124") is null, "awards off: Athens_124 stays description-only");
            Seq(new[] { 101, 113 }, OpeningMenu("Sparta_124", 67), "Sparta awarder captured menu unchanged");
            Seq(new[] { 101, 105, 106, 321 }, OpeningMenu("Athens_125", 67), "coordinator menu unchanged");
        }

        using (Rates((TroyEvent.EntrySwitch, 1m), (TroyEvent.AwardsSwitch, 1m)))
        {
            Seq(new[] { 200, 700, 501, 600, 202, 900 }, OpeningMenu("Athens_072", 1), "entry on: + To the Trojan Expedition");
            Seq(new[] { 101, 113 }, OpeningMenu("Athens_124", 67), "awards on: Athens_124 = the awarder's menu");
        }

        return Task.CompletedTask;
    }

    private static void Seq(int[] expected, int[] actual, string description) =>
        Check.True(expected.SequenceEqual(actual), $"{description}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}]");

    private static void Same(DateOnly? expected, DateOnly? actual, string description) =>
        Check.True(expected == actual, $"{description}: expected {expected}, actual {actual}");

    private static IDictionary? Functions(string key)
    {
        var resolve = typeof(GameClientHandler).GetMethod("ResolveScriptedNpcDialogues",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var npc = new NpcSpawnDefinition(1, "Athens", key, key + "_X", 5000, 0, 0, 5000, 17, 0, [], []);
        return resolve.Invoke(null, [npc]) as IDictionary;
    }

    private static int[] OpeningMenu(string key, int function)
    {
        var dialogue = Functions(key)?[function] ?? throw new InvalidOperationException($"{key} has no function {function}");
        return ((IEnumerable<int>)dialogue.GetType().GetProperty("OpeningMenu")!.GetValue(dialogue)!).ToArray();
    }

    private static IDisposable Rates(params (string Key, decimal Value)[] values)
    {
        var previous = ServerRates.Current;
        var merged = previous.Values.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            merged[key] = value;
        }

        ServerRates.Install(ServerRatesSnapshot.Create(merged, previous.Revision + 1, DateTimeOffset.UtcNow, "troy-check", "test"));
        return new Restore(previous);
    }

    private sealed class Restore(ServerRatesSnapshot previous) : IDisposable
    {
        public void Dispose() => ServerRates.Install(previous);
    }
}
