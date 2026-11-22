namespace Godswar.Server.State;

/// <summary>
/// B22 G2 (the admin d38, G1 question b22-g1-gems-use default A): the 12 GENERATED rare gems (items 4902-4913,
/// gear-tiers-v1) are the grade material for G21-G25 on the Titan / Primordial / Aether tier gear.
/// Rarity = grade band (the forge compares the CURRENT grade with Round, like every Emerald):
///   Radiant (4902-4905, rare, new-map monsters)    current G20-G21 -> G21, G22
///   Primal  (4906-4909, epic, new-map bosses)      current G22-G23 -> G23, G24
///   Olympian (4910-4913, god gems, world bosses)   current G24     -> G25
/// The four colours of a rarity work the same (colour is cosmetic, an assumption for the admin).
/// Tier gear at G20+ needs a gem: the Level 5 Emerald (4225, Round 10-24) is refused there, and gems
/// work only on tier gear. The client reads the same rows from BijouForge.xml
/// (tools/b22-g2/gen_gem_forge.py), so it shows the gem's rate; Round is per material on the client, so
/// the "L5 Emerald on tier gear at G20+" and "gem on ordinary gear" refusals are server-only and come
/// with a game-log hint while staging (GameClientHandler.Forging ForgeStagingHint).
/// No NextID on any gem row, so ForgeCompound never merges gems.
/// </summary>
internal static class RareGemForgeRules
{
    public const int EmeraldMaterialType = 3;
    public const short TierGemFromGrade = 20;

    private static readonly int[] RadiantRound = [20, 21];
    private static readonly int[] PrimalRound = [22, 23];
    private static readonly int[] OlympianRound = [24, 24];

    /// <summary>(ItemId, MaterialProyAdd, Round). Bonus 32 = the Level 5 Emerald's; the rarer gems a little more.</summary>
    public static IReadOnlyList<ForgingMaterialRule> All { get; } =
    [
        new(4902u, null, EmeraldMaterialType, 32, RadiantRound),
        new(4903u, null, EmeraldMaterialType, 32, RadiantRound),
        new(4904u, null, EmeraldMaterialType, 32, RadiantRound),
        new(4905u, null, EmeraldMaterialType, 32, RadiantRound),
        new(4906u, null, EmeraldMaterialType, 48, PrimalRound),
        new(4907u, null, EmeraldMaterialType, 48, PrimalRound),
        new(4908u, null, EmeraldMaterialType, 48, PrimalRound),
        new(4909u, null, EmeraldMaterialType, 48, PrimalRound),
        new(4910u, null, EmeraldMaterialType, 64, OlympianRound),
        new(4911u, null, EmeraldMaterialType, 64, OlympianRound),
        new(4912u, null, EmeraldMaterialType, 64, OlympianRound),
        new(4913u, null, EmeraldMaterialType, 64, OlympianRound),
    ];

    private static readonly HashSet<uint> GemIds = [.. All.Select(static rule => rule.ItemId)];

    private static readonly HashSet<uint> TierItemIds =
        [.. GearTierItemSeeds.ForgeBases.Select(static pair => pair.ItemId)];

    public static bool IsGem(uint itemId) => GemIds.Contains(itemId);

    public static bool IsTierGear(uint itemId) => TierItemIds.Contains(itemId);

    /// <summary>
    /// The extra B22 rule for a grade (Emerald-type) forge, checked after the material's own Round:
    /// a gem only on tier gear, and tier gear at G20+ only with a gem.
    /// </summary>
    public static EquipmentForgeValidationError CheckGrade(uint equipmentId, short currentGrade, uint materialId)
    {
        var gem = IsGem(materialId);
        var tier = IsTierGear(equipmentId);
        if (gem && !tier)
        {
            return EquipmentForgeValidationError.RareGemNeedsTierGear;
        }
        if (tier && currentGrade >= TierGemFromGrade && !gem)
        {
            return EquipmentForgeValidationError.TierGearNeedsRareGem;
        }
        return EquipmentForgeValidationError.None;
    }
}
