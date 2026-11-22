using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// B22 K1 (the admin d42/d43): skill levels 6-10 resolve through their family's level 5 (scaled x1.12 per level), and the
/// 19 world bosses drop a skill-book chest of their tier at 5% per kill, rolled in code. Each chest holds only books
/// of the opener's class at the tier's levels.
/// </summary>
internal static class SkillLevelExtensionChecks
{
    public const string CheckName = "B22 K1 skill levels 6-10 and world-boss skill-book chests";

    private const int Deaths = 200_000;

    public static Task RunAsync()
    {
        try
        {
            ServerRates.ResetToDefaults();
            CheckLevels();
            CheckChests();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void CheckLevels()
    {
        Check.True(SkillLevelExtension.TryGetLevelFive(349, out var five, out var steps) && five == 344 && steps == 5,
            "Sacred Zeal 10 (349) resolves through Sacred Zeal 5 (344), 5 steps");
        Check.True(!SkillLevelExtension.TryGetLevelFive(344, out _, out _) &&
                   !SkillLevelExtension.TryGetLevelFive(3065, out _, out _) &&
                   !SkillLevelExtension.TryGetLevelFive(105, out _, out _),
            "levels 1-5, portals and ids outside the 44 families are not extensions");
        Check.True(SkillStatusEffectCatalog.TryGet(349, out var zeal) && zeal.StatusId == 204 &&
                   zeal.HitBonus == 106 && zeal.CriticalAppendBonus == 42 && zeal.Priority == 10,
            $"Sacred Zeal 10: status 204, hit 60 -> 106, critical 24 -> 42, priority 10 ({zeal.HitBonus}/{zeal.CriticalAppendBonus}/{zeal.Priority})");
        Check.True(SkillStatusEffectCatalog.TryGet(99, out var ward) && ward.PhysicalDamageReduction == 0.4406m &&
                   ward.MagicDamageReduction == 0.2644m,
            $"Holy Ward 10: reductions 0.25/0.15 scaled ({ward.PhysicalDamageReduction}/{ward.MagicDamageReduction}) and capped at 0.5");
        Check.True(SkillDamageOverTimeCatalog.TryGet(549, out var lotus) && lotus.TickDamage == 1410 && lotus.StatusId == 1443,
            "Fire Lotus 10: burn tick 800 -> 1410");
        Check.True(FriendlySkillStatusCatalog.TryGetHealOverTime(769, out var grace) && grace.HpPerSecond == 3084,
            "Area Heal 10: heal over time 1750 -> 3084 per second");
        Check.True(SkillLevelExtension.LastIdOfFamily(250) == 259 && SkillLevelExtension.LastIdOfFamily(3060) == 3064,
            "zodiac grids count levels 6-10 as the family");
    }

    private static void CheckChests()
    {
        Check.True(Finalise2Rates.On(Finalise2RateKeys.SkillBookChestDrops), "drop.skill_book_chests defaults to on");
        Check.Equal(500, Finalise2Rates.Amount(Finalise2RateKeys.SkillBookChestChance), "chest chance defaults to 500 bp (5%)");
        Check.Equal(19, SkillBookChestLootRule.Chests.Count, "the 19 open-map world bosses");
        Check.True(SkillBookChestLootRule.TryGetChest("C_boss_dragon_013@21", 21, out var bahamut) && bahamut == 6523 &&
                   SkillBookChestLootRule.TryGetChest("A_boss_bull_001", 11, out var minos) && minos == 6520 &&
                   !SkillBookChestLootRule.TryGetChest("A_boss_bull_001", 207, out _) &&
                   !SkillBookChestLootRule.TryGetChest("A_normal_stub_001", 11, out _),
            "tiers: Bahamut Lv198 -> 6523, Minos Lv47 -> 6520; off its open map or another template: none");
        Install(dropChance: 3m, on: 1m, chance: 500m);
        var drops = 0;
        for (var i = 1; i <= Deaths; i++)
        {
            if (SkillBookChestLootRule.TryRoll("B_boss_dragon_001", 17, Death(i), ServerRates.Current, out var chest))
            {
                drops++;
                Check.Equal(6521u, chest, "Red Dragon Puluo Lv120 drops the Lv7 chest");
            }
        }
        Check.True(drops is >= 9_500 and <= 10_500, $"~5% with drop.chance 3 ({drops}/{Deaths})");
        Install(dropChance: 3m, on: 0m, chance: 500m);
        Check.True(!SkillBookChestLootRule.TryRoll("B_boss_dragon_001", 17, Death(1), ServerRates.Current, out _) &&
                   Enumerable.Range(1, 2000).All(i => !SkillBookChestLootRule.TryRoll("B_boss_dragon_001", 17, Death(i), ServerRates.Current, out _)),
            "switch off: no chests");
        Console.WriteLine($"    B22 chest rolls over {Deaths:N0} deaths: {drops} ({drops * 100.0 / Deaths:0.00}%)");

        // Chest contents: own class only, the tier's levels only.
        var books = SkillLevelItemSeeds.All.Where(static s => s.Id < 6520).ToDictionary(static s => (uint)s.Id);
        foreach (var (chestId, levels) in new (uint, int[])[] { (6520, [6]), (6521, [7]), (6522, [8]), (6523, [9, 10]) })
        {
            for (var profession = 0; profession < 4; profession++)
            {
                var rolled = Enumerable.Range(0, 400)
                    .SelectMany(_ => GiftPackPolicy.RollRewards(Pack(chestId), profession)).ToArray();
                Check.True(rolled.Length == 400 && rolled.All(r => books.TryGetValue(r.ItemId, out var seed) &&
                        seed.ClassIds.SequenceEqual(new short[] { (short)profession }) &&
                        levels.Contains(Level(seed))),
                    $"chest {chestId}, class {profession}: one book each, own class, levels {string.Join("/", levels)}");
            }
        }
    }

    private static GiftPackDefinition Pack(uint chestId) =>
        GiftPackPolicy.TryGetDefinitionForChecks(chestId, out var pack) ? pack
            : throw new InvalidOperationException($"chest {chestId} has no gift pack");

    private static int Level(ItemTemplateSeed seed) =>
        int.Parse(seed.DisplayName[(seed.DisplayName.LastIndexOf(' ') + 1)..]);

    private static Guid Death(int index) => new(index, 22, 13, 1, 2, 3, 4, 5, 6, 7, 8);

    private static void Install(decimal dropChance, decimal on, decimal chance) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            [
                KeyValuePair.Create(ServerRateKeys.DropChance, dropChance),
                KeyValuePair.Create(Finalise2RateKeys.SkillBookChestDrops, on),
                KeyValuePair.Create(Finalise2RateKeys.SkillBookChestChance, chance)
            ],
            1,
            DateTimeOffset.UtcNow,
            "check",
            "test"));
}
