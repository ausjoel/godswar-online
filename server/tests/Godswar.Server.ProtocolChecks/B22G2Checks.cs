using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// B22 G2: rare gems as the tier gear's G21-25 material.
internal static class B22G2Checks
{
    public const string GemCheckName = "B22 G2 rare gems forge tier gear G21-25";
    public const string MedalCheckName = "B22 G2 Valour Laurel, Aether Crest and Sanctum gem drops";
    public const string SanctumCheckName = "B22 G2 Aether Sanctum map set and scene key";
    public const string ClassSuitCheckName = "B22 G2 Class Suit NPC upgrade IV -> Titan -> Primordial -> Aether";

    private const uint TitanSpear = 1436;      // tier gear (base 1435 Class Suit IV spear)
    private const uint ClassSuitIvSpear = 1435;
    private const uint LevelFiveEmerald = 4225;
    private const uint LevelFiveCrystal = 4234;

    public static Task RunGemsAsync()
    {
        Check.Equal(12, RareGemForgeRules.All.Count, "12 gem rules");
        Check.True(RareGemForgeRules.All.All(static rule => rule.NextItemId is null && rule.MaterialType == 3),
            "gems are grade material without a compound NextID");
        Check.Equal(33, ForgingMaterialRuleCatalog.All.Count, "the shipped BijouForge list is unchanged");
        Check.True(RareGemForgeRules.IsTierGear(TitanSpear) && !RareGemForgeRules.IsTierGear(ClassSuitIvSpear),
            "Titan Spear is tier gear, the Class Suit IV spear is not");

        // Grade bands: Radiant 20-21, Primal 22-23, Olympian 24 (current grade).
        foreach (var (gem, grades) in new (uint, short[])[]
                 {
                     (4902, [20, 21]), (4905, [20, 21]), (4906, [22, 23]), (4909, [22, 23]), (4910, [24]), (4913, [24])
                 })
        {
            Check.True(ForgingMaterialRuleCatalog.TryGet(gem, out _), $"gem {gem} resolves as a forging material");
            for (short grade = 19; grade <= 25; grade++)
            {
                var ok = Forge(TitanSpear, grade, gem, out var calc, out var error);
                var expected = grades.Contains(grade);
                Check.Equal(expected, ok, $"gem {gem} on Titan Spear at G{grade} ({error})");
                if (ok)
                {
                    Check.Equal((short)(grade + 1), calc!.SuccessEquipment.Grade, $"gem {gem} raises G{grade} by one");
                    Check.Equal(TitanSpear, calc.SuccessEquipment.Id, "the item id is kept");
                    Check.Equal(100, calc.DisplayedProbability, "with 25 Level 5 crystals the rate is 100%");
                }
            }
        }

        // Tier gear at G20+ refuses the Level 5 Emerald; below G20 it still takes it.
        Check.True(Forge(TitanSpear, 19, LevelFiveEmerald, out _, out var e19), $"L5 Emerald forges Titan G19 ({e19})");
        Check.True(!Forge(TitanSpear, 20, LevelFiveEmerald, out _, out var e20) &&
                   e20 == EquipmentForgeValidationError.TierGearNeedsRareGem, $"L5 Emerald refused on Titan G20 ({e20})");
        // Ordinary gear is unchanged: L5 Emerald to G25, and gems refused.
        Check.True(Forge(ClassSuitIvSpear, 24, LevelFiveEmerald, out _, out var e24), $"L5 Emerald still forges Class Suit IV G24 ({e24})");
        Check.True(!Forge(ClassSuitIvSpear, 20, 4902, out _, out var eg) &&
                   eg == EquipmentForgeValidationError.RareGemNeedsTierGear, $"a gem is refused on ordinary gear ({eg})");
        // Gems never touch quality or level.
        foreach (var refusal in new[] { EquipmentForgeValidationError.TierGearNeedsRareGem, EquipmentForgeValidationError.RareGemNeedsTierGear })
        {
            var hint = GameClientHandlerHint(refusal);
            // The game-log line is a native centred announcement: at most 126 ASCII bytes (the rig caught 130).
            Check.True(hint is { Length: > 0 and <= 126 } && hint.All(static c => c < 128), $"hint for {refusal} fits the announcement ({hint?.Length})");
        }
        return Task.CompletedTask;
    }

    private static string? GameClientHandlerHint(EquipmentForgeValidationError error) =>
        Godswar.Server.Game.GameClientHandler.ForgeStagingHint(
            3, CompactItemEntry.Empty with { Id = TitanSpear, Quality = 1, Grade = 20, Stack = 1 }, error);

    private static bool Forge(uint equipment, short grade, uint material, out EquipmentForgeCalculation? calc,
        out EquipmentForgeValidationError error) =>
        EquipmentForgeCalculator.TryCalculate(
            new EquipmentForgeRequest(
                CompactItemEntry.Empty with { Id = equipment, Quality = 20, Grade = grade, Stack = 1 },
                new EquipmentForgeMaterialSelection(CompactItemEntry.Empty with { Id = material, Quality = 1, Grade = 1, Stack = 1 }, 1),
                new EquipmentForgeMaterialSelection(
                    CompactItemEntry.Empty with { Id = LevelFiveCrystal, Quality = 1, Grade = 1, Stack = 25 }, 25)),
            out calc,
            out error);

    private const int GearSlot = 3;
    private const int MedalSlot = 7;

    public static Task RunClassSuitAsync()
    {
        Check.Equal(186, ClassSuitTierUpgradeCatalog.All.Count, "one rule per tier item (62 x 3)");
        Check.True(ClassSuitTierUpgradeCatalog.All.Max(static rule => rule.MedalQuantity) <= 24, "at most 24 medals per piece");
        Check.True(ClassSuitTierUpgradeCatalog.TryResolve(ClassSuitIvSpear, out var r1) && r1.TargetItemId == TitanSpear &&
                   r1.MedalItemId == ClassSuitTierUpgradeCatalog.ValourLaurel && r1.MedalQuantity == 15 && r1.TargetMinLevel == 170,
            "IV spear -> Titan Spear for 15 Valour Laurels, Lv170");
        Check.True(ClassSuitTierUpgradeCatalog.TryResolve(TitanSpear, out var r2) && r2.TargetItemId == 1437 &&
                   r2.MedalItemId == ClassSuitTierUpgradeCatalog.ValourLaurel && r2.MedalQuantity == 24, "Titan -> Primordial for 24 Laurels");
        Check.True(ClassSuitTierUpgradeCatalog.TryResolve(1437, out var r3) && r3.TargetItemId == 1438 &&
                   r3.MedalItemId == ClassSuitTierUpgradeCatalog.AetherCrest && r3.MedalQuantity == 15, "Primordial -> Aether for 15 Crests");
        Check.True(!ClassSuitTierUpgradeCatalog.TryResolve(1438, out _), "Aether is the top");

        var catalog = CatalogWithTiers();
        var gear = CompactItemEntry.Empty with
        {
            Id = ClassSuitIvSpear, Quality = 20, Grade = 22, Stack = 1, Bound = 0,
            Attribute1 = 40, Attribute2 = 41, ClassAttribute1 = 210
        };
        var ok = Plan(catalog, gear, Medal(ClassSuitTierUpgradeCatalog.ValourLaurel, 20), 1, 170);
        Check.True(ok.Committed, $"IV -> Titan commits ({ok.RejectionReason})");
        Check.Equal(gear with { Id = TitanSpear }, ok.EquipmentAfter, "everything but the id is kept (quality, grade, attributes, class stone)");
        Check.Equal((short)5, KitBagSlots.GetItem(ok.UpdatedKitBag, MedalSlot).Stack, "15 of 20 Laurels used");

        var exact = Plan(catalog, gear, Medal(ClassSuitTierUpgradeCatalog.ValourLaurel, 15), 1, 170);
        Check.True(exact.Committed && KitBagSlots.GetItem(exact.UpdatedKitBag, MedalSlot).IsEmpty, "an exact stack empties the slot");

        Check.Equal((int)ClassSuitConversionStatus.InsufficientInsignia,
            (int)Plan(catalog, gear, Medal(ClassSuitTierUpgradeCatalog.ValourLaurel, 14), 1, 170).Status, "14 Laurels are too few");
        Check.Equal((int)ClassSuitConversionStatus.PlayerLevelTooLow,
            (int)Plan(catalog, gear, Medal(ClassSuitTierUpgradeCatalog.ValourLaurel, 15), 1, 169).Status, "Lv169 is too low for Titan");
        Check.Equal((int)ClassSuitConversionStatus.InvalidInsignia,
            (int)Plan(catalog, gear, Medal(ClassSuitTierUpgradeCatalog.AetherCrest, 50), 1, 200).Status, "Crests do not buy Titan");
        Check.Equal((int)ClassSuitConversionStatus.InvalidEquipment,
            (int)Plan(catalog, gear with { ElementalAttribute1 = 480 }, Medal(ClassSuitTierUpgradeCatalog.ValourLaurel, 15), 1, 170).Status,
            "an elemental attribute must be removed first");
        Check.True(!Plan(catalog, gear, Medal(ClassSuitTierUpgradeCatalog.ValourLaurel, 15), 0, 200).Committed,
            "another profession cannot upgrade the spear");
        Check.Equal((int)ClassSuitConversionStatus.UnsupportedSource,
            (int)Plan(catalog, gear with { Id = 1434 }, Medal(ClassSuitTierUpgradeCatalog.ValourLaurel, 15), 1, 200).Status,
            "Class Suit III is not a source");

        var titan = gear with { Id = TitanSpear };
        Check.True(Plan(catalog, titan, Medal(ClassSuitTierUpgradeCatalog.ValourLaurel, 24), 1, 185).EquipmentAfter.Id == 1437,
            "Titan -> Primordial at Lv185");
        var primordial = gear with { Id = 1437 };
        Check.True(Plan(catalog, primordial, Medal(ClassSuitTierUpgradeCatalog.AetherCrest, 15), 1, 200).EquipmentAfter.Id == 1438,
            "Primordial -> Aether at Lv200");
        Check.Equal((int)ClassSuitConversionStatus.InvalidInsignia,
            (int)Plan(catalog, primordial, Medal(ClassSuitTierUpgradeCatalog.ValourLaurel, 99), 1, 200).Status, "Laurels do not buy Aether");

        // Wire, menu and native results.
        Check.True(ClassSuitProtocol.MenuWithTierBeyond([100, 101, 102, 103, 104, 105, 106, 107, 108]).SequenceEqual(
            [100, 101, 102, 103, 104, 105, 106, 107, 108, 109]), "the NPC menu gains option 109");
        Check.True(ClassSuitProtocol.IsConversionOperation(109), "109 is a conversion operation");
        Check.Equal(177, ClassSuitNativeResults.Resolve(ClassSuitCommandOperation.UpgradeTierBeyond, ClassSuitCommandResultStatus.Succeeded), "success text 177");
        Check.Equal(175, ClassSuitNativeResults.Resolve(ClassSuitCommandOperation.UpgradeTierBeyond, ClassSuitCommandResultStatus.InsufficientMaterial), "too few medals 175");
        Check.Equal(176, ClassSuitNativeResults.Resolve(ClassSuitCommandOperation.UpgradeTierBeyond, ClassSuitCommandResultStatus.PlayerLevelTooLow), "level 176");
        return Task.CompletedTask;
    }

    private static ClassSuitConversionResult Plan(IItemTemplateCatalog catalog, CompactItemEntry gear, CompactItemEntry medal,
        byte profession, int level)
    {
        var bag = KitBagSlots.SetSlot(KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, GearSlot, gear.ToCompactString()),
            MedalSlot, medal.ToCompactString());
        return ClassSuitConversionPlanner.Create(catalog, bag, profession, level,
            new ClassSuitConversionRequest(ClassSuitConversionOperation.UpgradeTierBeyond,
                ClassSuitSlotSelection.Capture(bag, GearSlot), ClassSuitSlotSelection.Capture(bag, MedalSlot)));
    }

    private static CompactItemEntry Medal(uint id, int stack) =>
        CompactItemEntry.Empty with { Id = id, Quality = 1, Grade = 1, Stack = (short)stack };

    private static IItemTemplateCatalog CatalogWithTiers() =>
        PinnedItemTemplateCatalog.Create(
            "b22-g2-check",
            TestItemContent.Catalog.All.Concat(GearTierItemSeeds.All
                .Where(seed => !TestItemContent.Catalog.TryGet((uint)seed.Id, out _))
                .Select(static t => new ItemTemplateDefinition(
                    checked((uint)t.Id), t.Kind, t.NameKey, t.DisplayName, t.EquipmentSlot, t.ClassIds, t.MinLevel,
                    t.MaxLevel, t.Hand, t.SkillFlag, t.Texture, t.Icon, t.StatsJson))).ToArray(),
            [], [], [],
            ForgingMaterialCatalog.All,
            GearEnhancementMaterialCatalog.All,
            GearMentorMaterialCatalog.AttributeDusts,
            TestItemContent.GearMentorRecipes);

    public static Task RunMedalsAsync()
    {
        var rates = Godswar.Server.Application.Rates.ServerRates.Current;
        static Godswar.Server.Domain.World.Content.CapturedMonsterSpawn Mob(short map, string key, int level, byte camp = 2)
        {
            var packet = new byte[108];
            packet[5] = camp;
            BitConverter.GetBytes((uint)level).CopyTo(packet, 12);
            return new(map, "scene", key, "mob", 1, 0f, 0f, packet);
        }
        (uint, int)? Roll(Godswar.Server.Domain.World.Content.CapturedMonsterSpawn mob, int i) =>
            Godswar.Server.Application.World.Content.MedalLootRule.TryRoll(mob, new Guid(i, 7, 7, 1, 2, 3, 4, 5, 6, 7, 8), rates);

        Check.True(Roll(Mob(20, "A_boss_long_005", 150), 1) == (4900u, 5), "a Lv150 boss always drops 5 Laurels");
        var eliteHits = Enumerable.Range(1, 4000).Count(i => Roll(Mob(20, "C_elite_mage_005", 150), i) is { } d && d == (4900u, 1));
        Check.True(eliteHits is > 850 and < 1150, $"a Lv150 elite drops a Laurel about 25% of the time ({eliteHits}/4000)");
        Check.True(Enumerable.Range(1, 500).All(i => Roll(Mob(20, "C_normal_wolf_001", 150), i) is null), "normals drop no Laurel");
        Check.True(Enumerable.Range(1, 500).All(i => Roll(Mob(21, "A_boss_x", 171), i) is null &&
                                                   Roll(Mob(21, "A_boss_x", 139), i) is null), "outside Lv140-170: no Laurel");
        Check.True(Enumerable.Range(1, 500).All(i => Roll(Mob(7, "B_elite_greecewarrior_010", 145, camp: 0), i) is null),
            "faction guards drop no Laurel");
        var sanctum = (short)Godswar.Server.Application.WorldInstances.AetherSanctumPolicy.MapId;
        Check.True(Roll(Mob(sanctum, "B_boss_x", 200), 3) == (4901u, 5), "a Sanctum boss drops 5 Crests");
        Check.True(Roll(Mob(sanctum, "B_elite_x", 190), 3) == (4901u, 1), "a Sanctum elite drops 1 Crest");
        // Rare gems (d38): Radiant from normals (1%) and elites (25%), Primal from elites (5%) and bosses (1).
        (uint, int)? Gem(Godswar.Server.Domain.World.Content.CapturedMonsterSpawn mob, int i) =>
            Godswar.Server.Application.World.Content.SanctumGemLootRule.TryRoll(mob, new Guid(i, 9, 9, 1, 2, 3, 4, 5, 6, 7, 8), rates);
        Check.True(Gem(Mob(sanctum, "B_bossAS_skeleton_001", 198), 1) is { } bossGem && bossGem.Item2 == 1 &&
                   bossGem.Item1 is >= 4906 and <= 4909, "a Sanctum boss drops one Primal gem");
        var eliteGems = Enumerable.Range(1, 4000).Select(i => Gem(Mob(sanctum, "B_eliteAS_persiawarrior_001", 192), i)).Where(static g => g is not null).ToArray();
        var primal = eliteGems.Count(static g => g!.Value.Item1 >= 4906);
        Check.True(primal is > 120 and < 290 && eliteGems.Length - primal is > 800 and < 1100,
            $"Sanctum elites: Primal about 5% ({primal}), Radiant about 24% ({eliteGems.Length - primal}) of 4000");
        var normalGems = Enumerable.Range(1, 4000).Count(i => Gem(Mob(sanctum, "B_normalAS_robber_001", 192), i) is { } g && g.Item1 is >= 4902 and <= 4905);
        Check.True(normalGems is > 15 and < 75, $"Sanctum normals: Radiant about 1% ({normalGems}/4000)");
        Check.True(Enumerable.Range(1, 300).All(i => Gem(Mob(21, "C_boss_dragon_013", 198), i) is null), "no Sanctum gems on Olympus");
        var colours = Enumerable.Range(1, 400).Select(i => Gem(Mob(sanctum, "B_bossAS_skeleton_001", 198), i)!.Value.Item1).Distinct().Count();
        Check.Equal(4, colours, "all four colours drop");
        var normalHits = Enumerable.Range(1, 4000).Count(i => Roll(Mob(sanctum, "B_normal_x", 185), i) is not null);
        Check.True(normalHits is > 300 and < 500, $"a Sanctum normal drops a Crest about 10% of the time ({normalHits}/4000)");
        Check.True(Roll(Mob(sanctum, "B_boss_x", 150), 3)?.Item1 == 4901u, "the Sanctum drops Crests, never Laurels");
        return Task.CompletedTask;
    }

    public static Task RunSanctumAsync()
    {
        var stock = Enumerable.Range(0, 70).Concat(Enumerable.Range(200, 11)).Select(static v => (short)v).ToArray();
        Check.True(Godswar.Server.Application.World.GameplayMapSet.IsValid(stock), "the stock 81 maps are valid");
        Check.True(Godswar.Server.Application.World.GameplayMapSet.IsValid([.. stock, (short)70]), "plus the Aether Sanctum (70)");
        Check.True(!Godswar.Server.Application.World.GameplayMapSet.IsValid([.. stock, (short)71]), "no other extra map");
        Check.True(!Godswar.Server.Application.World.GameplayMapSet.IsValid(stock.Where(static v => v != 21)), "no missing stock map");
        Check.Equal("Aether_Sanctum", Godswar.Server.Game.GmNpcDialoguePolicy.ResolveSceneKey(70, []),
            "GM npcs (the exit teleporter) get the Sanctum's scene key");
        Check.Equal(70, (int)Godswar.Server.Application.WorldInstances.AetherSanctumPolicy.MapId, "map 70");
        return Task.CompletedTask;
    }
}
