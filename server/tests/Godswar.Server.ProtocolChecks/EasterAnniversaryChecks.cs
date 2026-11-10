using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Seasons;
using Godswar.Server.Domain.World.Content;
using S = Godswar.Server.Application.Seasons.EasterAnniversarySeason;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W3 seasons Easter + IGG Anniversary (F-26, F-134): windows, envoys, exchanges, drops.</summary>
internal static class EasterAnniversaryChecks
{
    public const string CheckName = "W3 seasons easter/igg: Easter window, envoys, egg exchanges, drops";

    public static Task RunAsync()
    {
        try
        {
            // Easter Sunday and the F-134 window (10 days before it, plus the day).
            Check.True(S.EasterSunday(2019) == new DateOnly(2019, 4, 21) && S.EasterSunday(2024) == new DateOnly(2024, 3, 31) &&
                       S.EasterSunday(2025) == new DateOnly(2025, 4, 20) && S.EasterSunday(2026) == new DateOnly(2026, 4, 5) &&
                       S.EasterSunday(2027) == new DateOnly(2027, 3, 28), "Easter Sunday 2019/2024-2027");
            var sunday = new DateTimeOffset(2027, 3, 28, 0, 0, 0, TimeSpan.Zero);
            Check.True(S.InEasterWindow(sunday.AddDays(-10)) && S.InEasterWindow(sunday.AddHours(23)) &&
                       !S.InEasterWindow(sunday.AddDays(-10).AddSeconds(-1)) && !S.InEasterWindow(sunday.AddDays(1)),
                "window = [Sunday - 10 d, Sunday + 1 d)");

            // Gates: no row = closed; on with no dates + auto dates = the window only; auto 0 = the editor alone.
            SeasonalEvents.Install([]);
            Check.True(S.EasterSwitchOn && S.AnniversarySwitchOn, "switches default to 1");
            Check.True(!S.EasterOpen(sunday) && !S.AnniversaryOpen(sunday), "closed with no season row");
            SeasonalEvents.Install(SeasonalEvents.Parse("easter:1:0:0:1,igg_anniversary:1:0:0:1"));
            Check.True(S.EasterOpen(sunday.AddDays(-3)) && !S.EasterOpen(sunday.AddDays(20)), "auto dates: window only");
            Check.True(S.AnniversaryOpen(sunday.AddDays(20)), "anniversary: editor dates alone");
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(S.EasterAutoDatesKey, 0m)], 1, DateTimeOffset.UtcNow, "check", "test"));
            Check.True(S.EasterOpen(sunday.AddDays(20)), "auto dates 0: open whenever the season is on");
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(S.EasterSwitchKey, 0m), KeyValuePair.Create(S.AnniversarySwitchKey, 0m)], 2,
                DateTimeOffset.UtcNow, "check", "test"));
            Check.True(!S.EasterOpen(sunday) && !S.AnniversaryOpen(sunday), "switch 0: closed");
            Check.True(!S.Drops(11, Guid.NewGuid(), sunday).Any(), "switch 0: no drops");
            ServerRates.ResetToDefaults();

            // Envoys at map entry.
            NpcSpawnDefinition Npc(string key, uint id) => new(1, "Athens", key, key + "_X", id, 0, 0, id, 0x111, 0, [], []);
            var roster = new List<NpcSpawnDefinition> { Npc("Athens_128", 5266), Npc("Athens_119", 5257), Npc("Athens_001", 5100) };
            var open = S.ApplyRoster(1, roster, sunday);
            Check.True(open.Single(static n => n.NpcKey == "Athens_117") is
                       { ObjectId: 5256, InteractionId: 5256, AppearanceType: 0x111, TemplateKey: "Athens_117_Male32", X: 60f, Z: -148f },
                "Athens_117 placed with the capture's appearance at the moved spot, id 5256 (0x111, 60,-148)");
            Check.True(open.Any(static n => n.NpcKey == "Athens_128") && open.Any(static n => n.NpcKey == "Athens_119"),
                "Easter + Anniversary envoys kept while open");
            Check.True(!S.ApplyRoster(0, [], sunday).Any(), "Sparta_117 is published by the map, not added");
            NpcSpawnDefinition sparta117 = new(0, "Sparta", "Sparta_117", "Sparta_117_Male32", 5114, 64f, -152f, 5114, 0x11, 3f, [], []);
            Check.True(S.ApplyRoster(0, [sparta117], sunday).Single() is
                       { ObjectId: 5114, InteractionId: 5114, AppearanceType: 0x11, Facing: 3f, X: 60f, Z: -148f },
                "published Sparta_117 moved to 60,-148, id 5114 and appearance 0x11 kept");
            Check.True(!S.ApplyRoster(0, [sparta117], sunday.AddDays(30)).Any(), "Sparta_117 hidden outside the Easter window");
            var later = S.ApplyRoster(1, roster, sunday.AddDays(30));
            Check.True(!later.Any(static n => n.NpcKey is "Athens_128" or "Athens_117") &&
                       later.Any(static n => n.NpcKey == "Athens_119") && later.Any(static n => n.NpcKey == "Athens_001"),
                "outside the Easter window: Easter envoys hidden, the rest kept");
            Check.True(S.ApplyRoster(1, [Npc("Athens_099", 5256)], sunday).Single(static n => n.NpcKey == "Athens_117")
                       .ObjectId == 5760, "5256 taken: Athens_117 falls back to 5760");
            Check.True(!S.ApplyRoster(1, [Npc("Athens_098", 5256), Npc("Athens_099", 5760)], sunday)
                .Any(static n => n.NpcKey == "Athens_117"), "every candidate taken: not placed");

            // The published Athens roster: the capture's map-1 placements (as ApplyToMap leaves them) plus the actor
            // catalog's emulator ids (Athens_116 = 5255, Athens_118 = 5257, ...) as object and interaction ids.
            var published = new List<NpcSpawnDefinition>();
            foreach (var key in Godswar.Server.Infrastructure.WorldContent.CapturedNpcPlacements.CapturedKeysFor(1))
                if (key != "Athens_117" &&
                    Godswar.Server.Infrastructure.WorldContent.CapturedNpcPlacements.TryFind(key, out var c))
                    published.Add(new(1, "Athens", key, c.TemplateKey, c.ObjectId, c.X, c.Z, c.ObjectId, c.AppearanceType, c.Facing, [], []));
            var actorIds = Godswar.Server.State.NpcActorPlacementCatalog.All.Where(static a => a.MapId == 1)
                .Select(static a => a.SourceObjectId).ToHashSet();
            var placedIds = S.ApplyRoster(1, published, sunday).Single(static n => n.NpcKey == "Athens_117");
            var publishedIds = published.SelectMany(static n => new[] { n.ObjectId, n.InteractionId }).ToHashSet();
            Check.True(!publishedIds.Contains(placedIds.ObjectId) && !actorIds.Contains(placedIds.ObjectId) &&
                       placedIds.ObjectId != 5255, $"Athens_117's id {placedIds.ObjectId} collides with no published Athens id");
            Check.True(S.Athens117Ids.All(id => !actorIds.Contains(id) && id is not (>= 5700 and <= 5733) and not 5740 and not 5741 and not (>= 5750 and <= 5753)) &&
                       S.Athens117Ids[^1] is var last && !publishedIds.Contains(last),
                "the fallback id is used by no capture, actor-catalog, event-NPC , Christmas or Halloween/May Day id");
            // The moved spot: no captured or actor-catalog NPC of either capital within 4 units, and clear of the IGG envoy
            // by more than the capture's one 4-unit diagonal (rig: that diagonal made her pick box win the bunny's clicks).
            float Dist(float x, float z) => MathF.Sqrt((x - S.BunnyEnvoyX) * (x - S.BunnyEnvoyX) + (z - S.BunnyEnvoyZ) * (z - S.BunnyEnvoyZ));
            var neighbours = new List<(string Key, float X, float Z)>();
            foreach (short map in new short[] { 0, 1 })
                foreach (var key in Godswar.Server.Infrastructure.WorldContent.CapturedNpcPlacements.CapturedKeysFor(map))
                    if (key is not ("Athens_117" or "Sparta_117") &&
                        Godswar.Server.Infrastructure.WorldContent.CapturedNpcPlacements.TryFind(key, out var c))
                        neighbours.Add((key, c.X, c.Z));
            neighbours.AddRange(Godswar.Server.State.NpcActorPlacementCatalog.All
                .Where(static a => a.MapId is 0 or 1 && a.NpcKey is not ("Athens_117" or "Sparta_117"))
                .Select(static a => (a.NpcKey, a.X, a.Z)));
            var nearest = neighbours.MinBy(n => Dist(n.X, n.Z));
            Check.True(Dist(nearest.X, nearest.Z) >= 4f,
                $"bunny spot clear: nearest capital NPC {nearest.Key} is {Dist(nearest.X, nearest.Z):0.0} units away");
            Check.True(Godswar.Server.Infrastructure.WorldContent.CapturedNpcPlacements.TryFind("Athens_119", out var igg) &&
                       Dist(igg.X, igg.Z) >= 8f, "bunny at least 8 units from the IGG Anniversary Envoy");
            SeasonalEvents.Install(SeasonalEvents.Parse("easter:1:0:0:1"));
            Check.True(!S.ApplyRoster(1, roster, sunday).Any(static n => n.NpcKey == "Athens_119"),
                "Anniversary envoy hidden while its season is closed");

            // Easter Envoy: REVIVE1001-1007.
            Check.True(S.EggExchange(1) == (1, 30, 1001) && S.EggExchange(2) == (2, 25, 1002) &&
                       S.EggExchange(6) == (6, 5, 1006) && S.EggExchange(7) == (1, 1, 1007) && S.EggExchange(30) == (1, 1, 1007),
                "egg exchanges: k eggs -> 35-5k pills, then 1 -> 1");
            Check.True(S.EggExchange(1).Pills <= 99, "pills within a stack of 99");
            Check.True(S.PackageContents(false).Length == 3 && S.PackageContents(true).Any(static c => c.ItemId == 10154) &&
                       S.PackageContents(true).All(static c => c.Count <= 99), "Easter Package contents");

            // Bunny Envoy: rotating prizes that grow every round of four.
            Check.True(S.BunnyReward(1, 50) == (S.BunnyPrize.Experience, 25_000L) &&
                       S.BunnyReward(2, 50) == (S.BunnyPrize.TalentPoints, 5L) &&
                       S.BunnyReward(3, 50) == (S.BunnyPrize.PetExperience, 50_000L) &&
                       S.BunnyReward(4, 50) == (S.BunnyPrize.ZodiacEnergy, 10L) &&
                       S.BunnyReward(5, 50) == (S.BunnyPrize.Experience, 50_000L) &&
                       S.BunnyReward(40, 50) == (S.BunnyPrize.ZodiacEnergy, 100L), "rotation EXP/TP/pet/Zodiac, x(1+(k-1)/4)");
            Check.True(S.BunnyCagesPerClaim <= 99 && S.BunnyDailyExchanges == 40, "40 cages (<= 99), 40 turn-ins a day");

            // Drops.
            var all = Enumerable.Range(0, 300).SelectMany(static _ => S.Roll(Guid.NewGuid(), 100, 100, 100, 100)).ToList();
            Check.Equal(1_200, all.Count, "100 %: four drops per kill");
            Check.True(all.Select(static d => d.ItemId).Distinct().OrderBy(static i => i).SequenceEqual([3985u, 3995u, 9008u, 9009u]) &&
                       all.Select(static d => d.Index).Distinct().Count() == 4, "items 3985/9009/9008/3995 on their own indexes");
            Check.True(!Enumerable.Range(0, 300).SelectMany(static _ => S.Roll(Guid.NewGuid(), 0, 0, 0, 0)).Any(), "0 %: nothing");
            var three = Enumerable.Range(0, 20_000).Count(static _ => S.Roll(Guid.NewGuid(), 3, 0, 0, 0).Count > 0);
            Check.True(three is > 400 and < 800, $"3 % is about 3 % ({three} of 20,000)");
            SeasonalEvents.Install(SeasonalEvents.Parse("easter:0:0:0:1,igg_anniversary:1:0:0:1"));
            var key5 = Enumerable.Range(0, 2_000).SelectMany(_ => S.Drops(5, Guid.NewGuid(), sunday)).Count();
            var key11 = Enumerable.Range(0, 2_000).SelectMany(_ => S.Drops(11, Guid.NewGuid(), sunday)).ToList();
            Check.True(key5 == 0 && key11.Count > 0 && key11.All(static d => d.ItemId == 3995),
                "Key Pieces only on Marathon / Peloponnese, no eggs with Easter off");
            // IGG envoy (rig 2026-11-09): the stock Set_MessageBox has no IGG branch, so the function click and the
            // go-again click (sub -1, no path) are each the purchase; other words never charge.
            Check.True(S.IsAnniversaryPurchase(-1, []) && S.IsAnniversaryPurchase(0, []) &&
                       S.IsAnniversaryPurchase(100, []) && S.IsAnniversaryPurchase(-1, [100]),
                "IGG: sub -1 / no path and 100 are the purchase");
            Check.True(!S.IsAnniversaryPurchase(101, []) && !S.IsAnniversaryPurchase(-1, [101]) &&
                       !S.IsAnniversaryPurchase(197, []), "IGG: other words are not a purchase");
            Check.True(new[] { S.AnniversaryObtained, S.AnniversaryClosed, S.AnniversaryNoGold, S.AnniversaryBagFull }
                .SequenceEqual([100, 197, 198, 199]), "IGG: replies are NpcFunIgg.lua's Index 1 pages 100/197/198/199");
            var templates = TestItemContent.Catalog;
            // 10154 (Easter Bunny Egg) is not in the pinned test catalog; on live it is a mall item (bought 2026-09-14,
            // GameClientHandler.Mall.cs) and gift pack 3977 grants it, so the database has its template.
            var missing = new uint[] { 4174, 10087, 4514, 4008, 4036, 3989, 3985, 9008, 9009, 3995 }
                .Where(id => !templates.TryGet(id, out _)).ToArray();
            Check.True(missing.Length == 0, $"every granted / dropped item has a template (missing: {string.Join(',', missing)})");
            Check.True(ServerRateCatalog.Definitions.Count(static d => d.Key.StartsWith("season.easter") ||
                       d.Key.StartsWith("season.igg")) == S.Definitions.Count, "rates registered");
        }
        finally
        {
            ServerRates.ResetToDefaults();
            SeasonalEvents.Install([]);
        }

        return Task.CompletedTask;
    }
}
