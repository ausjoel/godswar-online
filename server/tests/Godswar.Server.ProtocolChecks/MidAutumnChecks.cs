using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Seasons;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W3 seasons mid-autumn (F-29): Lunar Priest pages and amounts, portion drops, mooncake EXP, compound.</summary>
internal static class MidAutumnChecks
{
    public const string CheckName = "W3 seasons mid-autumn: Lunar Priest, mooncake drops, mooncake EXP, compound";

    public static Task RunAsync()
    {
        var t0 = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        try
        {
            // Season gate: no row = closed; the switch defaults to 1.
            SeasonalEvents.Install([]);
            Check.True(MidAutumnSeason.SwitchOn, "season.mid_autumn defaults to 1");
            Check.True(!MidAutumnSeason.IsOpen(t0.AddDays(1)), "closed while the season is off");
            Check.True(!Enumerable.Range(0, 200).SelectMany(_ => MidAutumnSeason.Drops(Guid.NewGuid(), t0.AddDays(1), 100, 100)).Any(),
                "no drops while the season is closed");
            SeasonalEvents.Install(SeasonalEvents.Parse($"mid_autumn:1:{t0.ToUnixTimeSeconds()}:{t1.ToUnixTimeSeconds()}:1"));
            Check.True(MidAutumnSeason.IsOpen(t0.AddDays(1)) && !MidAutumnSeason.IsOpen(t1.AddDays(1)),
                "open inside its dates only");

            // Drops: 100 % = one portion of the six every kill, all six kinds seen; 0 % = nothing.
            var portions = Enumerable.Range(0, 600)
                .SelectMany(_ => MidAutumnSeason.Drops(Guid.NewGuid(), t0.AddDays(1), 100, 0)).ToList();
            Check.Equal(600, portions.Count, "100 %: one drop per kill");
            Check.True(portions.All(static d => d.ItemId is >= 3800 and <= 3805 && d.Index == 0) &&
                       portions.Select(static d => d.ItemId).Distinct().Count() == 6, "portions 3800-3805, all six kinds");
            var cakes = Enumerable.Range(0, 200)
                .SelectMany(_ => MidAutumnSeason.Drops(Guid.NewGuid(), t0.AddDays(1), 0, 100)).ToList();
            Check.True(cakes.Count == 200 && cakes.All(static d => d.ItemId == 3920 && d.Index == 1),
                "100 % Delicate Mooncake: 3920 on its own index");
            Check.True(!Enumerable.Range(0, 200).SelectMany(_ => MidAutumnSeason.Drops(Guid.NewGuid(), t0.AddDays(1), 0, 0)).Any(),
                "0 %: nothing");
            var fivePercent = Enumerable.Range(0, 20_000)
                .Count(_ => MidAutumnSeason.Roll(Guid.NewGuid(), 5, 0).Any());
            Check.True(fivePercent is > 700 and < 1_300, $"5 % portion rate is about 5 % ({fivePercent} of 20,000)");
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(MidAutumnSeason.SwitchKey, 0m)], 1, DateTimeOffset.UtcNow, "check", "test"));
            Check.True(!MidAutumnSeason.IsOpen(t0.AddDays(1)) &&
                       !MidAutumnSeason.Drops(Guid.NewGuid(), t0.AddDays(1), 100, 100).Any(), "switch 0: closed, no drops");
            var templates = TestItemContent.Catalog;
            Check.True(!StatusExperienceBoostPolicy.TryResolveItem(templates, 3806, out _) &&
                       !PlayerExperienceItemPolicy.TryResolveAmount(templates, 3811, out _),
                "switch 0: the mooncakes do nothing");
            Check.True(StatusExperienceBoostPolicy.TryResolveItem(templates, 4504, out _),
                "switch 0 leaves Passion Rose alone");
            ServerRates.ResetToDefaults();

            // Lunar Priest pages (NpcFunMoon0.lua): 1 EXP, 2 TP, 3 pet EXP, 4 Zodiac.
            Check.True(MidAutumnSeason.PageTwo(MidAutumnSeason.Reward.Experience).SequenceEqual([100, 101, 102, 103]) &&
                       MidAutumnSeason.PageTwo(MidAutumnSeason.Reward.TalentPoints).SequenceEqual([110, 101, 102, 103]) &&
                       MidAutumnSeason.PageTwo(MidAutumnSeason.Reward.PetExperience).SequenceEqual([120, 101, 102, 103]) &&
                       MidAutumnSeason.PageTwo(MidAutumnSeason.Reward.ZodiacEnergy).SequenceEqual([130, 101, 102, 103]),
                "page 2 = the choice's text/Exchange-1 entry then 3 / 5 / 10");
            Check.True(MidAutumnSeason.RewardOf(2) == MidAutumnSeason.Reward.TalentPoints &&
                       MidAutumnSeason.RewardOf(3) == MidAutumnSeason.Reward.PetExperience &&
                       MidAutumnSeason.RewardOf(5) == MidAutumnSeason.Reward.None, "BUTTON2 is TP, BUTTON3 pet EXP");
            Check.True(MidAutumnSeason.CakesOf(110) == (1, MidAutumnSeason.Reward.TalentPoints) &&
                       MidAutumnSeason.CakesOf(101) == (3, MidAutumnSeason.Reward.None) &&
                       MidAutumnSeason.CakesOf(102).Cakes == 5 && MidAutumnSeason.CakesOf(103).Cakes == 10 &&
                       MidAutumnSeason.CakesOf(104).Cakes == 0, "entry -> cakes");
            Check.Equal(1_000L * 50, MidAutumnSeason.Amount(MidAutumnSeason.Reward.Experience, 1, 50), "1 cake: 1,000 EXP x level");
            Check.Equal(1_000L * 50 * 15, MidAutumnSeason.Amount(MidAutumnSeason.Reward.Experience, 10, 50), "10 cakes pay as 15");
            Check.Equal(60L, MidAutumnSeason.Amount(MidAutumnSeason.Reward.TalentPoints, 5, 50), "5 cakes pay as 6 (10 TP each)");
            Check.Equal(2_000L * 80 * 3, MidAutumnSeason.Amount(MidAutumnSeason.Reward.PetExperience, 3, 80), "pet EXP x level");
            Check.Equal(20L, MidAutumnSeason.Amount(MidAutumnSeason.Reward.ZodiacEnergy, 1, 80), "20 Zodiac energy per cake");
            Check.True(MidAutumnSeason.IsLunarPriest("Athens_032") && MidAutumnSeason.IsLunarPriest("Sparta_026") &&
                       !MidAutumnSeason.IsLunarPriest("Athens_026"), "both priests, not Demetrius");
            Check.True(SeasonalEvents.SeasonOf("Athens_032") == MidAutumnSeason.Key &&
                       SeasonalEvents.SeasonOf("Sparta_026") == MidAutumnSeason.Key, "both priests are mid_autumn envoys");

            // Placement (rig 2026-11-09: no Athens priest at all). Athens_032 has no published row on map 1 and the
            // published Athens_033 holds its captured 5172, so the season places it on the free id 5770 with Athens'
            // clickable word 0x111 at the capture's 73,-168 facing 1.7. Sparta_026 keeps its published 5024 / 0x11.
            NpcSpawnDefinition Row(short map, string scene, string key, string template, uint id, uint word, float x,
                float z, float facing) => new(map, scene, key, template, id, x, z, id, word, facing, [], []);
            var athens = new List<NpcSpawnDefinition>
            {
                Row(1, "Athens", "Athens_033", "Athens_033_Male35", 5172, 0x111, 178f, -32f, 2.3f),
                Row(1, "Athens", "Athens_059", "Athens_059_Belle2", 5197, 0x111, 73f, -175.8f, 1.7f)
            };
            var athensOpen = SeasonalEvents.Apply(1, athens, t0.AddDays(1));
            var priest = athensOpen.SingleOrDefault(static npc => npc.NpcKey == "Athens_032");
            Check.True(priest is not null && priest.ObjectId == 5770 && priest.InteractionId == 5770 &&
                       priest.AppearanceType == 0x111 && priest.X == 73f && priest.Z == -168f && priest.Facing == 1.7f &&
                       priest.TemplateKey == "Athens_032_Male10" && priest.SceneKey == "Athens",
                "season open: Athens_032 placed as 5770 / 0x111 at 73,-168 facing 1.7");
            Check.Equal(SeasonalEvents.AthensLunarPriestId, 5770u, "the Athens priest's id constant");
            Check.True(athensOpen.Select(static npc => npc.ObjectId).Distinct().Count() == athensOpen.Count,
                "the placed priest keeps every object id on the map unique");
            Check.True(!SeasonalEvents.Apply(1, athens, t1.AddDays(1)).Any(static npc => npc.NpcKey == "Athens_032"),
                "season closed: no Athens priest");
            Check.True(!SeasonalEvents.Apply(1,
                    [.. athens, Row(1, "Athens", "Athens_999", "Athens_999_X", 5770, 0x111, 10f, -10f, 0f)],
                    t0.AddDays(1)).Any(static npc => npc.NpcKey == "Athens_032"),
                "5770 taken on the map: the priest is not placed (no duplicate id)");
            var sparta = new List<NpcSpawnDefinition>
            {
                Row(0, "Sparta", "Sparta_026", "Sparta_026_Male10", 5024, 0x11, 73f, -168f, 1.7f),
                Row(0, "Sparta", "Sparta_059", "Sparta_059_Belle2", 5056, 0x11, 73f, -178f, 1.7f)
            };
            var spartaOpen = SeasonalEvents.Apply(0, sparta, t0.AddDays(1));
            Check.True(spartaOpen.Count(static npc => npc.NpcKey == "Sparta_026") == 1 &&
                       spartaOpen.Single(static npc => npc.NpcKey == "Sparta_026") is { ObjectId: 5024, AppearanceType: 0x11 },
                "Sparta_026 keeps its published 5024 and Sparta's word 0x11 (same as the working Wedding Priest)");
            Check.True(!SeasonalEvents.Apply(0, sparta, t1.AddDays(1)).Any(static npc => npc.NpcKey == "Sparta_026"),
                "season closed: the Sparta priest is hidden");

            // Mooncake EXP (Status.ini 501-504, Kind 14) and Bean / Red Mooncake flat EXP.
            foreach (var (item, status, bp, hours) in new (uint, int, int, int)[]
                     { (3806, 501, 3_000, 8), (3807, 502, 6_000, 2), (3808, 503, 6_000, 1), (3809, 504, 10_000, 1) })
            {
                Check.True(StatusExperienceBoostPolicy.TryResolveItem(templates, item, out var boost) &&
                           boost.StatusId == status && boost.BonusBasisPoints == bp &&
                           boost.Kind == ExperienceBoostKinds.Consumable &&
                           boost.OnlineTicks == hours * TimeSpan.TicksPerHour,
                    $"mooncake {item}: +{bp / 100} % EXP for {hours} h on the Consumable channel");
            }

            Check.True(PlayerExperienceItemPolicy.TryResolveAmount(templates, 3810, out var bean) && bean == 500 &&
                       PlayerExperienceItemPolicy.TryResolveAmount(templates, 3811, out var red) && red == 2_000,
                "Bean / Red Mooncake: 500 / 2,000 EXP");
            Check.True(!PlayerExperienceItemPolicy.TryResolveAmount(templates, 10551, out _),
                "pet book 10551 (also skill 4805) is not an EXP item");

            // Compound: six portions of a kind -> its mooncake (BijouForge NextID), all six pairs.
            for (uint portion = 3800; portion <= 3805; portion++)
            {
                var bag = Enumerable.Repeat(CompactItemEntry.Empty, 96).ToArray();
                var stack = CompactItemEntry.Empty with { Id = portion, Quality = 1, Grade = 1, Stack = 6 };
                bag[0] = stack;
                var plan = ForgeCompoundPlanner.PlanGemCompound(templates, bag,
                    Enumerable.Repeat(new ForgeCompoundSelection(0, stack), 6).ToArray());
                Check.True(templates.TryGet(portion + 6, out _) && plan.Committed && plan.Result.Id == portion + 6 &&
                           plan.Result.Stack == 1, $"6 x {portion} -> {portion + 6}");
            }
        }
        finally
        {
            ServerRates.ResetToDefaults();
            SeasonalEvents.Install([]);
        }

        return Task.CompletedTask;
    }
}
