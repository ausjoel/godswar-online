namespace Godswar.Server.State;

/// <summary>One step past Class Suit IV: the GENERATED tier gear of G1 (Titan V Lv170, Primordial VI Lv185, Aether VII Lv200).</summary>
internal sealed record ClassSuitTierUpgradeRule(
    uint SourceItemId,
    uint TargetItemId,
    int TargetTier,
    uint MedalItemId,
    int MedalQuantity,
    int TargetMinLevel);

/// <summary>
/// B22 G2 (the admin d36): the Class Suit NPC (function 37, new option 109) upgrades a Class Suit IV piece to Titan (V),
/// Titan to Primordial (VI) and Primordial to Aether (VII). Valour Laurels (4900, Lv140-170 bosses and elites) pay
/// for V and VI, Aether Crests (4901, the Lv170-200 instance) for VII. The price per piece is the piece's own Class
/// Suit insignia cost c (1-3, ClassSuitConversionCatalog) times 5 / 8 / 5 medals, so a whole worn set (about 11
/// pieces, sum of c about 22) costs about 110 Laurels for Titan, 176 more for Primordial and 110 Crests for Aether.
/// At most 24 medals per piece: always one stack (stacks stay at 99).
/// The item keeps everything it has (quality, grade, attributes, holy sockets, class stone); a piece carrying an
/// elemental attribute is refused (the database allows elemental attributes only on Class Suit III/IV ids).
/// Kept apart from ClassSuitConversionCatalog on purpose: that catalog validates the 248 stock Class Suit ids exactly,
/// and tier gear cannot go back to common equipment.
/// </summary>
internal static class ClassSuitTierUpgradeCatalog
{
    public const uint ValourLaurel = 4900;
    public const uint AetherCrest = 4901;
    public const int TitanTier = 5;
    public const int PrimordialTier = 6;
    public const int AetherTier = 7;
    public static readonly int[] MedalsPerCost = [5, 8, 5]; // V, VI, VII

    private static readonly IReadOnlyDictionary<uint, ClassSuitTierUpgradeRule> BySource = Build();

    public static IReadOnlyCollection<ClassSuitTierUpgradeRule> All => (IReadOnlyCollection<ClassSuitTierUpgradeRule>)BySource.Values;

    public static bool TryResolve(uint sourceItemId, out ClassSuitTierUpgradeRule rule) =>
        BySource.TryGetValue(sourceItemId, out rule!);

    public static bool IsTierGear(uint itemId) =>
        GearTierItemSeeds.ForgeBases.Any(pair => pair.ItemId == itemId);

    private static Dictionary<uint, ClassSuitTierUpgradeRule> Build()
    {
        var levels = GearTierItemSeeds.All.ToDictionary(static seed => (uint)seed.Id, static seed => seed.MinLevel ?? 0);
        var rules = new Dictionary<uint, ClassSuitTierUpgradeRule>();
        foreach (var group in GearTierItemSeeds.ForgeBases.GroupBy(static pair => pair.BaseItemId))
        {
            var baseId = group.Key;
            if (!ClassSuitConversionCatalog.TryResolveSuit(baseId, out var branch, out var tier) ||
                tier != ClassSuitTier.TierIV)
            {
                throw new InvalidOperationException($"Tier gear base {baseId} is not a Class Suit IV item.");
            }

            var chain = group.Select(static pair => pair.ItemId).OrderBy(id => levels[id]).ToArray();
            if (chain.Length != 3 || levels[chain[0]] != 170 || levels[chain[1]] != 185 || levels[chain[2]] != 200)
            {
                throw new InvalidOperationException($"Tier gear for base {baseId} is not one Titan/Primordial/Aether chain.");
            }

            var source = baseId;
            for (var step = 0; step < 3; step++)
            {
                var quantity = branch.InsigniaCost * MedalsPerCost[step];
                rules.Add(source, new ClassSuitTierUpgradeRule(
                    source,
                    chain[step],
                    TitanTier + step,
                    step < 2 ? ValourLaurel : AetherCrest,
                    quantity,
                    levels[chain[step]]));
                source = chain[step];
            }
        }

        if (rules.Values.Any(static rule => rule.MedalQuantity is < 1 or > 99))
        {
            throw new InvalidOperationException("A tier upgrade costs more medals than one stack holds.");
        }

        return rules;
    }
}
