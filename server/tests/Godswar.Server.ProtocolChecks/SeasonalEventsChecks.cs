using Godswar.Server.Application.Seasons;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W3 seasonal events (F-26): windows, envoy gating at map entry, Thanksgiving drops.</summary>
internal static class SeasonalEventsChecks
{
    public const string CheckName = "W3 seasonal events: windows, envoys, Thanksgiving drops";

    public static Task RunAsync()
    {
        var t0 = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero);
        var rows = SeasonalEvents.Parse(
            $"thanksgiving:1:{t0.ToUnixTimeSeconds()}:{t1.ToUnixTimeSeconds()}:1,halloween:0:0:0:1,christmas:1:0:0:1,bad");
        Check.Equal(3, rows.Count, "three rows parsed, a malformed one skipped");
        try
        {
            SeasonalEvents.Install(rows);
            Check.True(SeasonalEvents.IsOpen("thanksgiving", t0.AddDays(3)), "open inside its dates");
            Check.True(!SeasonalEvents.IsOpen("thanksgiving", t1), "closed at its end");
            Check.True(SeasonalEvents.IsOpen("thanksgiving", t0.AddYears(1).AddDays(1)), "yearly: open again next year");
            Check.True(!SeasonalEvents.IsOpen("halloween", t0), "switched off = closed");
            Check.True(SeasonalEvents.IsOpen("christmas", t0), "switched on with no dates = open");
            Check.True(!SeasonalEvents.IsOpen("easter", t0), "no row = closed");

            NpcSpawnDefinition Npc(string key, uint id) => new(1, "Athens", key, key + "_X", id, 0, 0, id, 17, 0, [], []);
            var roster = new[] { Npc("Athens_053", 5192), Npc("Athens_084", 5223), Npc("Athens_001", 5100) };
            var during = SeasonalEvents.Apply(1, roster, t0.AddDays(1)).Select(static n => n.NpcKey).ToList();
            Check.True(!during.Contains("Athens_053") && during.Contains("Athens_084") && during.Contains("Athens_001") &&
                       during.Contains("Athens_012"),
                "Halloween envoy hidden off-season; Festival Envoy and others kept; Thanksgiving Envoy added");
            Check.True(SeasonalEvents.Apply(1, roster, t0.AddDays(1)).Single(static n => n.NpcKey == "Athens_012") is
                       { ObjectId: 5152, AppearanceType: 0x111, TemplateKey: "Athens_012_MaleMerchant3" }, "Athens Thanksgiving Envoy = the capture's 5152 / 0x111");
            Check.True(!SeasonalEvents.Apply(1, roster, t1.AddDays(1)).Any(static n => n.NpcKey == "Athens_012"),
                "no Thanksgiving Envoy after the season");
            Check.True(SeasonalEvents.Apply(0, [], t0.AddDays(1)).Any(static n => n.NpcKey == "Sparta_012"), "Sparta's on map 0");

            var drops = Enumerable.Range(0, 2_000).Select(static i => new Guid(i, 7, 9, 1, 2, 3, 4, 5, 6, 7, (byte)(i % 251)))
                .SelectMany(g => SeasonalEvents.ThanksgivingDrops(11, g, t0.AddDays(1), 100, 0)).ToList();
            Check.True(drops.Count == 2_000 && drops.All(static d => d.ItemId is 3945 or 3946 or 3947),
                "100 %: every Marathon kill drops one turkey item");
            Check.True(!SeasonalEvents.ThanksgivingDrops(2, Guid.NewGuid(), t0.AddDays(1), 100, 100).Any() &&
                       !SeasonalEvents.ThanksgivingDrops(11, Guid.NewGuid(), t1.AddDays(1), 100, 100).Any(),
                "no drops on other maps or off-season");
            Check.True(SeasonalEvents.ThanksgivingDrops(13, Guid.NewGuid(), t0.AddDays(1), 0, 100).Single().ItemId == 3948,
                "card chance 100 % on Peloponnese gives a Greetings Card");

            CheckChristmas(t0);
        }
        finally
        {
            SeasonalEvents.Install([]);
        }

        return Task.CompletedTask;
    }

    // W3 Christmas: Santa's placement, antler drops, season year, model thresholds and page numbers.
    private static void CheckChristmas(DateTimeOffset t0)
    {
        var santa = SeasonalEvents.Apply(1, [], t0).Single(static n => n.NpcKey == "Athens_035");
        Check.True(santa is { ObjectId: 5740, InteractionId: 5740, AppearanceType: 0x111, TemplateKey: "Athens_035_FemMale9",
            X: 68f, Z: -152f }, "Athens Santa Claus: 5740 / 0x111 at 68,-152");
        Check.True(SeasonalEvents.Apply(0, [], t0).Single(static n => n.NpcKey == "Sparta_035") is
            { ObjectId: 5741, AppearanceType: 0x11, X: 68f, Z: -152f }, "Sparta Santa Claus: 5741 / 0x11 at 68,-152");
        Check.True(SeasonalEvents.Apply(1, [], t0).Single(static n => n.NpcKey == "Athens_012") is { X: 64f, Z: -161f },
            "the Thanksgiving Envoy keeps 64,-161");

        var random = new Random(26);
        var deaths = Enumerable.Range(0, 2_000).Select(_ =>
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        }).ToList();
        Check.True(deaths.All(g => SeasonalEvents.ChristmasDrops(1, "A_normal_deer_001", g, t0, 100).Single().ItemId == 4005),
            "100 %: every Little Deer in Athens drops an Antler");
        var half = deaths.Count(g => SeasonalEvents.ChristmasDrops(4, "A_normal_xedeer_001", g, t0, 50).Any());
        Check.True(half is > 800 and < 1200, $"50 %: about half the Vicious Fawns drop one ({half} of 2,000)");
        Check.True(!SeasonalEvents.ChristmasDrops(11, "A_normal_deer_001", deaths[0], t0, 100).Any() &&
                   !SeasonalEvents.ChristmasDrops(1, "A_normal_deer_006", deaths[0], t0, 100).Any() &&
                   !SeasonalEvents.ChristmasDrops(1, "A_normal_deer_001", deaths[0], t0, 0).Any(),
            "no Antlers from other maps, other monsters or at 0 %");

        var dec = new DateTimeOffset(2026, 12, 20, 0, 0, 0, TimeSpan.Zero);
        var jan = new DateTimeOffset(2027, 1, 3, 0, 0, 0, TimeSpan.Zero);
        SeasonalEvents.Install(SeasonalEvents.Parse($"christmas:1:{dec.ToUnixTimeSeconds()}:{jan.ToUnixTimeSeconds()}:1"));
        Check.True(!SeasonalEvents.ChristmasDrops(1, "A_normal_deer_001", deaths[0], dec.AddDays(-1), 100).Any(),
            "no Antlers before the window");
        Check.True(SeasonalEvents.SeasonYear("christmas", jan.AddDays(-1)) == 2026 &&
                   SeasonalEvents.SeasonYear("christmas", dec.AddYears(1).AddDays(1)) == 2027,
            "a window over New Year keeps one season year; next year's window is the next year");
        Check.True(SeasonalEvents.SeasonYear("easter", jan) == 2027, "no row: the UTC year");

        Check.True(ChristmasRules.Thresholds(100).SequenceEqual([5000, 10000, 20000, 30000, 50000]) &&
                   ChristmasRules.Thresholds(10).SequenceEqual([500, 1000, 2000, 3000, 5000]),
            "thresholds: the client's at 100 %, a tenth at the default 10 %");
        var tiny = ChristmasRules.Thresholds(0);
        Check.True(tiny.Zip(tiny.Skip(1)).All(static p => p.First < p.Second) && tiny[0] >= 1,
            "thresholds stay at least 1 and strictly increasing");
        int[] th = [500, 1000, 2000, 3000, 5000];
        Check.True(ChristmasRules.Level(0, th) == 0 && ChristmasRules.Level(500, th) == 1 && ChristmasRules.Level(4999, th) == 4 &&
                   ChristmasRules.Level(5000, th) == 5, "model level from points");
        Check.True(ChristmasRules.ProgressPage(1200, th).SequenceEqual([2108, 1_200_116]),
            "progress: 1000*level+108 and 1000*points+116");
        Check.True(ChristmasRules.ProgressPage(5000, th).SequenceEqual([107]), "level 5: 107 only, never 5108 (nil in luatext)");
        Check.True(ChristmasRules.TurnInPage(37, 10, th).SequenceEqual([37_105]) &&
                   ChristmasRules.TurnInPage(100, 10, th).SequenceEqual([104]) &&
                   ChristmasRules.TurnInPage(0, 5000, th).SequenceEqual([106]),
            "turn-in page: the 105 form, 104 at the daily 100, 106 when finished");
        Check.True(ChristmasRules.ClaimMenu(0, th).SequenceEqual([109]) && ChristmasRules.ClaimMenu(600, th).SequenceEqual([6, 7, 8, 9, 10]) &&
                   ChristmasRules.ClaimLevel(6) == 1 && ChristmasRules.ClaimLevel(10) == 5 && ChristmasRules.ClaimLevel(11) == 0,
            "claim menu and claim buttons 6-10 = levels 1-5");
        Check.True(ChristmasRules.Room(4990, th) == 10 && ChristmasRules.Room(6000, th) == 0, "the model takes only what it needs");
        Check.True(ChristmasRules.TurnInReward(5, 2000) == (10_000L, 1) && ChristmasRules.TurnInReward(100, 2000) == (200_000L, 10),
            "turn-in pays EXP per Antler and 1 Talent Point per 10 (at least 1)");
    }
}
