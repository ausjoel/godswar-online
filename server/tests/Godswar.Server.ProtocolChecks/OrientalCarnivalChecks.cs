using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Seasons;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;
using S = Godswar.Server.Application.Seasons.OrientalCarnivalSeason;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W3 seasons Oriental Cuisine Master + Carnival Envoy (F-26, F-134): gates, NPCs, scrolls, wishes, drops.</summary>
internal static class OrientalCarnivalChecks
{
    public const string CheckName = "W3 seasons oriental/carnival: Cuisine Master scrolls, recipe wish, Carnival wishes, drops";

    public static Task RunAsync()
    {
        try
        {
            var at = new DateTimeOffset(2027, 9, 10, 12, 0, 0, TimeSpan.Zero);

            // Gates: no row = closed; switch 0 = closed.
            SeasonalEvents.Install([]);
            Check.True(S.OrientalSwitchOn && S.CarnivalSwitchOn, "switches default to 1");
            Check.True(!S.OrientalOpen(at) && !S.CarnivalOpen(at), "closed with no season row");
            SeasonalEvents.Install(SeasonalEvents.Parse("oriental_cuisine:1:0:0:1,carnival:1:0:0:1"));
            Check.True(S.OrientalOpen(at) && S.CarnivalOpen(at), "open: editor season on, switch 1");
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(S.OrientalSwitchKey, 0m), KeyValuePair.Create(S.CarnivalSwitchKey, 0m)], 1,
                DateTimeOffset.UtcNow, "check", "test"));
            Check.True(!S.OrientalOpen(at) && !S.CarnivalOpen(at), "switch 0: closed");
            Check.True(!S.Drops(5, Guid.NewGuid(), at).Any(), "switch 0: no recipe drops");
            ServerRates.ResetToDefaults();

            // NPCs at map entry: the Athens pair placed from the capture, the Sparta pair published by the map.
            NpcSpawnDefinition Npc(short map, string key, uint id) =>
                new(map, map == 1 ? "Athens" : "Sparta", key, key + "_X", id, 0, 0, id, 0x111, 0, [], []);
            var athens = S.ApplyRoster(1, [Npc(1, "Athens_001", 5100), Npc(1, "Athens_125", 5264)], at);
            Check.True(athens.Single(static n => n.NpcKey == "Athens_031") is
                       { ObjectId: 5171, InteractionId: 5171, AppearanceType: 0x111, TemplateKey: "Athens_031_Sicily3",
                           X: 64f, Z: -182f }, "Athens_031 placed from the capture (5171 / 0x111, 64,-182)");
            Check.True(athens.Single(static n => n.NpcKey == "Athens_126") is
                       { ObjectId: 5265, InteractionId: 5265, AppearanceType: 0x111, TemplateKey: "Athens_126_FemVillager3",
                           X: 64f, Z: -156f },
                "Athens_126 placed (5265: the captured 5264 is the published Athens_125's / 0x111, 64,-156)");
            Check.True(!S.ApplyRoster(1, [Npc(1, "Athens_099", 5171)], at).Any(static n => n.NpcKey == "Athens_031"),
                "Athens_031 not placed when 5171 is taken");
            var sparta = S.ApplyRoster(0, [Npc(0, "Sparta_025", 5023), Npc(0, "Sparta_126", 5123)], at);
            Check.True(sparta.Count == 2, "Sparta_025 / Sparta_126 kept while open, nothing added on map 0");
            SeasonalEvents.Install(SeasonalEvents.Parse("carnival:1:0:0:1"));
            var closed = S.ApplyRoster(0, [Npc(0, "Sparta_025", 5023), Npc(0, "Sparta_126", 5123)], at);
            Check.True(closed.Count == 1 && closed[0].NpcKey == "Sparta_126", "Cuisine Master hidden while closed");
            Check.True(!S.ApplyRoster(1, [], at).Any(static n => n.NpcKey == "Athens_031") &&
                       S.ApplyRoster(1, [], at).Any(static n => n.NpcKey == "Athens_126"), "Athens: only the open one placed");
            SeasonalEvents.Install(SeasonalEvents.Parse("oriental_cuisine:1:0:0:1,carnival:1:0:0:1"));

            // Mysterious Oriental Scrolls: item -> quest as the client data says, and the band picks.
            foreach (var item in S.ScrollItems)
            {
                Check.True(QuestRuntimeRequirements.ScrollItems.TryGetValue((uint)item, out var quest) &&
                           S.IsScrollQuest(quest) && StarterQuestChain.Find(quest) is { IsQuestScroll: true },
                    $"scroll {item} opens a scroll quest");
            }

            Check.Equal(18, S.ScrollQuests.Distinct().Count(), "18 scroll quests");
            Check.True(S.PickScroll(true, 50, 0, 0) is 4660 or 4661 && S.PickScroll(true, 50, 10_000, 0) is 4660 or 4661,
                "Athens Lv50 -> I/II");
            Check.True(S.PickScroll(true, 75, 12_345, 0) is 4662 or 4663, "Athens Lv75 -> III/IV");
            Check.True(S.PickScroll(true, 100, 777, 0) is 4664 or 4665, "Athens Lv100 -> V/VI");
            Check.True(S.PickScroll(true, 125, 99_999, 0) is 4814 or 4815, "Athens Lv125 -> VIII/IX");
            Check.True(S.PickScroll(false, 30, 5, 0) is 4667 or 4668 && S.PickScroll(false, 125, 5, 0) is 4816 or 4817,
                "Sparta bands");
            Check.True(S.PickScroll(true, 50, 0, 100) == 4666 && S.PickScroll(false, 50, 0, 100) == 4673,
                "100 %: the 2-cake scroll VII");
            var spread = Enumerable.Range(0, 4_000).Select(static i => S.PickScroll(false, 70, i * 7_919, 0)).Distinct()
                .OrderBy(static i => i).ToArray();
            Check.True(spread.SequenceEqual([4669, 4670]), "both band scrolls are picked");
            Check.True(S.MooncakesFor(326) == 2 && S.MooncakesFor(1326) == 2 && S.MooncakesFor(1320) == 1 &&
                       S.MooncakesFor(475) == 1 && S.MooncakesFor(999) == 0, "mooncakes per scroll quest");

            // A picked scroll's quest can be accepted at that level (not only within nine levels of its minimum).
            foreach (var (camp, level) in new[] { (true, 50), (true, 88), (true, 118), (true, 128), (false, 59), (false, 90) })
            {
                for (var roll = 0; roll < 60_000; roll += 9_973)
                {
                    var scroll = (uint)S.PickScroll(camp, level, roll, 10);
                    var step = StarterQuestChain.Find(QuestRuntimeRequirements.ScrollItems[scroll])!.Value;
                    Check.True(GameClientHandler.IsWithinAcceptBand(level, step),
                        $"Lv{level} can accept quest {step.QuestId} of scroll {scroll}");
                }
            }

            var unrelated = StarterQuestChain.Find(249)!.Value; // another scroll quest keeps the nine-level band
            Check.True(!GameClientHandler.IsWithinAcceptBand(60, unrelated), "other scroll quests keep the nine-level band");
            var built = S.ScrollQuests.Where(static q => QuestRewardItemCatalog.ResolveBuiltIn(q)
                .Any(static item => item.ItemId == S.DelicateMooncake)).ToArray();
            Check.True(built.Length == 0, $"no scroll quest's built-in reward menu holds 3920, so the hand-in hook pays it " +
                $"(menus with it: {string.Join(',', built)})");

            // Free recipe wish (NpcFunFood).
            Check.True(S.WishOpening(0) == 3001 && S.WishOpening(2) == 1001 && S.WishOpening(5) == 1, "remaining*1000+1");
            Check.True(S.WishWait(125) == 12_502 && S.WishWait(0) == 102 && S.WishWait(99_999) == 360_002,
                "seconds*100+2 (minutes/seconds split by the client)");
            Check.True(S.WishRecipesMax <= 99, "wish stack within 99");

            // Carnival wishes: costs from the en_us labels, items exist, counts within 99.
            Check.True(S.PageOneButtons.SequenceEqual([120, 121, 122, 123, 124, 126]), "page 1 buttons (125 overlaps 124)");
            foreach (var category in new[] { 124, 125, 126 })
                Check.True(S.PageTwoButtons(category).All(static b => S.FindWish(b) is { SecondPage: true }),
                    $"page 2 of {category} is all wishes");
            Check.True(S.PageOneButtons.Where(static b => b < 124).All(static b => S.FindWish(b) is { SecondPage: false, Cost: 500 }),
                "120-123 cost 500");
            Check.True(S.FindWish(210) is { Cost: 40 } && S.FindWish(230) is { Cost: 30, Count: 25, Items: [4230] } &&
                       S.FindWish(233) is { Cost: 300, Count: 3 } && S.FindWish(235) is { Cost: 500, Items: [9962] } &&
                       S.FindWish(225) is { Items: [10299] } && S.FindWish(999) is null, "label costs and prizes");
            Check.True(S.Wishes.All(static w => w.Count is >= 1 and <= 99 && w.Items.Length > 0), "wish counts within 99");
            // Recipe buttons: Alchemy, Scholarship, Blacksmithing, Mythcrafting = professions 1, 4, 2, 3.
            foreach (var (button, profession) in new[] { (210, 1), (211, 4), (212, 2), (213, 3) })
            {
                var items = S.FindWish(button)!.Value.Items;
                Check.True(items.All(item => ProfessionBlueprintCatalog.All.Any(b => b.RecipeItemId == (uint)item &&
                    b.Profession == profession)), $"recipes of {button} are profession {profession}");
            }

            Check.Equal(28, S.Wishes.Where(static w => w.Button is >= 210 and <= 213).Sum(static w => w.Items.Length),
                "28 recipes (NF_L0_TZN13xxx)");
            Check.True(S.Replies(false) == (925, 924, 922, 941, 923, 8) && S.Replies(true) == (926, 927, 930, 940, 933, 7),
                "Index 2 / Index 3 result numbers");
            Check.True(S.CreditsLine(1234, 5) == 1_234_005 && S.CreditsLine(-3, 8) == 8, "credits lines");
            Check.True(S.CreditsFor(115) == 11 && S.CreditsFor(15_550) == 1_555 && S.CreditsFor(0) == 0,
                "10 credits per 100 Gold, rounded down");
            Check.True(S.Odds(S.Tier.Rare) == 2_500 && S.Odds(S.Tier.Common) == 5_000, "odds 25 % / 50 %");

            // Drops.
            var all = Enumerable.Range(0, 500).SelectMany(static _ => S.Roll(Guid.NewGuid(), 100)).ToList();
            Check.True(all.Count == 500 && all.All(static d => d.ItemId == 3919), "100 %: one recipe per kill");
            Check.True(!Enumerable.Range(0, 500).SelectMany(static _ => S.Roll(Guid.NewGuid(), 0)).Any(), "0 %: nothing");
            var four = Enumerable.Range(0, 20_000).Count(static _ => S.Roll(Guid.NewGuid(), 4).Count > 0);
            Check.True(four is > 600 and < 1_000, $"4 % is about 4 % ({four} of 20,000)");
            Check.True(S.Drops(5, Guid.NewGuid(), at).All(static d => d.ItemId == 3919), "open: recipes only");
            SeasonalEvents.Install(SeasonalEvents.Parse("carnival:1:0:0:1"));
            Check.True(!Enumerable.Range(0, 2_000).SelectMany(_ => S.Drops(5, Guid.NewGuid(), at)).Any(),
                "Oriental Cuisine closed: no recipe drops");

            // Every granted / dropped item has a template.
            var templates = TestItemContent.Catalog;
            var granted = S.Wishes.SelectMany(static w => w.Items).Concat(S.ScrollItems)
                .Concat([S.GreekCuisineRecipe, S.DelicateMooncake]).Distinct().ToArray();
            var missing = granted.Where(id => !templates.TryGet((uint)id, out _)).ToArray();
            Check.True(missing.Length == 0, $"every granted item has a template (missing: {string.Join(',', missing)})");
            Check.True(ServerRateCatalog.Definitions.Count(static d => d.Key.StartsWith("season.oriental") ||
                       d.Key.StartsWith("season.carnival")) == S.Definitions.Count, "rates registered");
        }
        finally
        {
            ServerRates.ResetToDefaults();
            SeasonalEvents.Install([]);
        }

        return Task.CompletedTask;
    }
}
