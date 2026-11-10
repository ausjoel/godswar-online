using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Seasons;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using S = Godswar.Server.Application.Seasons.HalloweenMayDaySeason;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W3 seasons Halloween + May Day (F-26, F-134): windows, envoys, exchanges, the tree rules, drops.</summary>
internal static class HalloweenMayDayChecks
{
    public const string CheckName = "W3 seasons halloween/may day: windows, envoys, candy exchange, faction tree, drops";

    public static Task RunAsync()
    {
        try
        {
            // F-134 windows: the 10 days before the event day, plus the day.
            var halloween = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
            var mayDay = new DateTimeOffset(2027, 5, 1, 0, 0, 0, TimeSpan.Zero);
            Check.True(S.InWindow(halloween.AddDays(-10), 10, 31) && S.InWindow(halloween.AddHours(23), 10, 31) &&
                       !S.InWindow(halloween.AddDays(-10).AddSeconds(-1), 10, 31) && !S.InWindow(halloween.AddDays(1), 10, 31),
                "Halloween window = [Oct 21, Nov 1) UTC");
            Check.True(S.InWindow(mayDay.AddDays(-10), 5, 1) && S.InWindow(mayDay.AddHours(23), 5, 1) &&
                       !S.InWindow(mayDay.AddDays(-10).AddSeconds(-1), 5, 1) && !S.InWindow(mayDay.AddDays(1), 5, 1),
                "May Day window = [Apr 21, May 2) UTC");
            Check.True(S.IsEventDay(mayDay.AddHours(20), 5, 1) && !S.IsEventDay(mayDay.AddDays(-1), 5, 1), "event day");

            // Gates: no row = closed; on with no dates + auto dates = the window only; auto 0 = the editor alone.
            SeasonalEvents.Install([]);
            Check.True(S.HalloweenSwitchOn && S.MayDaySwitchOn, "switches default to 1");
            Check.True(!S.HalloweenOpen(halloween) && !S.MayDayOpen(mayDay), "closed with no season row");
            SeasonalEvents.Install(SeasonalEvents.Parse("halloween:1:0:0:1,may_day:1:0:0:1"));
            Check.True(S.HalloweenOpen(halloween.AddDays(-3)) && !S.HalloweenOpen(halloween.AddDays(20)) &&
                       S.MayDayOpen(mayDay.AddDays(-3)) && !S.MayDayOpen(mayDay.AddDays(20)), "auto dates: window only");
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(S.HalloweenAutoDatesKey, 0m), KeyValuePair.Create(S.MayDayAutoDatesKey, 0m)], 1,
                DateTimeOffset.UtcNow, "check", "test"));
            Check.True(S.HalloweenOpen(halloween.AddDays(20)) && S.MayDayOpen(mayDay.AddDays(20)),
                "auto dates 0: open whenever the season is on");
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(S.HalloweenSwitchKey, 0m), KeyValuePair.Create(S.MayDaySwitchKey, 0m)], 2,
                DateTimeOffset.UtcNow, "check", "test"));
            Check.True(!S.HalloweenOpen(halloween) && !S.MayDayOpen(mayDay), "switch 0: closed");
            Check.True(!S.Drops(Guid.NewGuid(), halloween).Any(), "switch 0: no drops");
            ServerRates.ResetToDefaults();

            CheckRoster(halloween, mayDay);

            // Halloween Envoy pages (NpcFunHallo.lua).
            Check.True(S.ExchangeMenu(0).SequenceEqual([0, 5, 6, 7, 8, 8001]), "exchange menu = the captured [0,5,6,7,8,8001]");
            Check.True(S.ExchangeMenu(7).SequenceEqual([0, 5, 6, 7, 8, 1001]) && S.ExchangeMenu(8).SequenceEqual([201]) &&
                       S.ExchangeMenu(30).SequenceEqual([201]), "1 left = 1001; none left = 201");
            Check.True(S.CandyPrizeAmount(S.CandyPrize.Experience, 50) == 50_000 &&
                       S.CandyPrizeAmount(S.CandyPrize.TalentPoints, 50) == 10 &&
                       S.CandyPrizeAmount(S.CandyPrize.PetExperience, 50) == 100_000 &&
                       S.CandyPrizeAmount(S.CandyPrize.ZodiacEnergy, 50) == 20, "candy prizes from the GENERATED defaults");
            Check.True(S.WearsHalloweenSuit("[1001]#[8230,0,0,0,0,0,1,1,1,1]#[]") && S.WearsHalloweenSuit("#####[8237]") &&
                       !S.WearsHalloweenSuit("[1001]#[]#[3700]") && !S.WearsHalloweenSuit(""), "suit worn = 8230/8237/8238 equipped");

            // May Day tree rules.
            Check.True(S.TreeTarget == 300 && S.FertiliserDailyCap == 20, "tree 300, 20 a day (GENERATED)");
            Check.True(S.FertiliserTaken(1, 0, 0) == 1 && S.FertiliserTaken(5, 18, 0) == 2 && S.FertiliserTaken(5, 0, 298) == 2 &&
                       S.FertiliserTaken(5, 20, 0) == 0 && S.FertiliserTaken(5, 0, 300) == 0 && S.FertiliserTaken(0, 0, 0) == 0,
                "taken = held within the daily cap and the tree's room");
            Check.True(S.EventDayWinner(10, 5) == 0 && S.EventDayWinner(5, 10) == 1 && S.EventDayWinner(7, 7) == -1,
                "event-day winner: more Fertiliser, a tie for both");
            Check.True(S.ClaimReward(true, true, 40) == (14000, 80_000L) && S.ClaimReward(false, false, 40) == (14001, 0L) &&
                       S.ClaimReward(false, true, 40) == (14001, 80_000L), "Great pack to the winners, Small otherwise; EXP if grown");

            // Drops.
            var all = Enumerable.Range(0, 300).SelectMany(static _ => S.Roll(Guid.NewGuid(), 100, 100)).ToList();
            Check.Equal(600, all.Count, "100 %: two drops per kill");
            Check.True(all.Select(static d => d.ItemId).Distinct().OrderBy(static i => i).SequenceEqual([3943u, 4507u]) &&
                       all.Select(static d => d.Index).Distinct().Count() == 2, "items 4507/3943 on their own indexes");
            Check.True(!Enumerable.Range(0, 300).SelectMany(static _ => S.Roll(Guid.NewGuid(), 0, 0)).Any(), "0 %: nothing");
            var four = Enumerable.Range(0, 20_000).Count(static _ => S.Roll(Guid.NewGuid(), 4, 0).Count > 0);
            Check.True(four is > 600 and < 1_000, $"4 % is about 4 % ({four} of 20,000)");
            SeasonalEvents.Install(SeasonalEvents.Parse("halloween:1:0:0:1"));
            Check.True(Enumerable.Range(0, 2_000).SelectMany(_ => S.Drops(Guid.NewGuid(), halloween)).Any() &&
                       !Enumerable.Range(0, 2_000).SelectMany(_ => S.Drops(Guid.NewGuid(), halloween.AddDays(5))).Any(),
                "drops only inside the Halloween window");

            // Items, stacks and rates.
            var templates = TestItemContent.Catalog;
            var missing = new uint[] { 4507, 8230, 10085, 3943, 3987, 14000, 14001 }
                .Where(id => !templates.TryGet(id, out _)).ToArray();
            Check.True(missing.Length == 0, $"every granted / dropped item has a template (missing: {string.Join(',', missing)})");
            Check.True(S.CandyPerExchange <= 99 && S.CandyExchangesPerDay <= 99, "no stack above 99");
            Check.True(ServerRateCatalog.Definitions.Count(static d => d.Key.StartsWith("season.halloween") ||
                       d.Key.StartsWith("season.may_day")) == S.Definitions.Count, "rates registered");
        }
        finally
        {
            ServerRates.ResetToDefaults();
            SeasonalEvents.Install([]);
        }

        return Task.CompletedTask;
    }

    private static void CheckRoster(DateTimeOffset halloween, DateTimeOffset mayDay)
    {
        // The real Athens roster through world entry's own steps: is the Halloween Envoy published, at which id?
        var athens = NpcContentBaselineV1.LoadDefinitions().Where(static npc => npc.MapId == 1)
            .Select(CapitalNpcServiceProtocol.ApplyCapturedSpawnCompatibility).ToList();
        var published = CapturedNpcPlacementPolicy.ApplyToMap(athens)
            .Where(static npc => !WorldObjectIds.IsReservedForPlayer(npc.ObjectId)).ToList();
        Check.True(athens.Count > 100, $"the pinned baseline carries the Athens city roster ({athens.Count} rows)");
        Check.True(published.SingleOrDefault(static npc => npc.NpcKey == "Athens_053") is { ObjectId: 5191 },
            "the real Athens roster publishes Athens_053 at the captured 5191 (5192 is Athens_054, skipped)");
        Check.True(published.All(static npc => npc.ObjectId != 5192), "nothing published at the player's 0x1448");

        SeasonalEvents.Install(SeasonalEvents.Parse("halloween:1:0:0:1,may_day:1:0:0:1"));
        NpcSpawnDefinition Npc(short map, string key, uint id) =>
            new(map, map == 1 ? "Athens" : "Sparta", key, key + "_X", id, 0, 0, id, 0x111, 0, [], []);
        var roster = new List<NpcSpawnDefinition> { Npc(1, "Athens_053", 5191), Npc(1, "Athens_001", 5100) };
        var inHalloween = S.ApplyRoster(1, roster, halloween);
        Check.True(inHalloween.Single(static n => n.NpcKey == "Athens_053").ObjectId == 5191 &&
                   !inHalloween.Any(static n => S.IsMayDayEnvoy(n.NpcKey)), "Halloween: envoy kept at 5191, no May Day Envoy");
        var lost = S.ApplyRoster(1, [Npc(1, "Athens_001", 5100)], halloween);
        Check.True(lost.Single(static n => n.NpcKey == "Athens_053") is
                   { ObjectId: S.AthensHalloweenFallbackId, InteractionId: S.AthensHalloweenFallbackId, X: 68f, Z: -161f,
                       TemplateKey: "Athens_053_Hallo", AppearanceType: 0x111 },
            "a lost Halloween Envoy is re-added at 5750 with the captured spot");
        Check.True(!S.ApplyRoster(1, roster, halloween.AddDays(5)).Any(static n => n.NpcKey == "Athens_053"),
            "outside the window the Halloween Envoy is hidden");
        var inMay = S.ApplyRoster(1, roster, mayDay);
        Check.True(inMay.Single(static n => n.NpcKey == "Athens_MayDay") is
                   { ObjectId: 5752, InteractionId: 5752, X: 159f, Z: 76f, TemplateKey: "Athens_084_FemMale29",
                       AppearanceType: 0x111 } && !inMay.Any(static n => n.NpcKey == "Athens_053"),
            "May Day: envoy at (159,76) as 5752; Halloween Envoy hidden");
        Check.True(S.ApplyRoster(0, [], mayDay).Single() is { NpcKey: "Sparta_MayDay", ObjectId: 5753, AppearanceType: 0x11 },
            "Sparta May Day Envoy 5753 / 0x11");
        Check.True(!S.ApplyRoster(1, [Npc(1, "Athens_099", 5752)], mayDay).Any(static n => n.NpcKey == "Athens_MayDay"),
            "not placed when 5752 is taken");
        Check.True(!S.ApplyRoster(2, [], mayDay).Any(), "only the capitals");
        Check.True(!published.Any(static npc => npc.ObjectId is >= 5750 and <= 5753 || npc.InteractionId is >= 5750 and <= 5753),
            "5750-5753 are free on the real Athens roster");
        Check.True(WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(159f, 76f, out _) &&
                   WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(68f, -161f, out _),
            "(159,76) and (68,-161) are on the world grid");
    }
}
