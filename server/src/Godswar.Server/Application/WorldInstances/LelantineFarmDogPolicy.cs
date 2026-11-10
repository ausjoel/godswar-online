namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// The capturable dogs on the Lelantine Farm. The templates are the map's own
/// (<c>Monster.ini</c> <c>A_normal{A,B,C,D}_{male,famale}_001</c>, published with is_pet = true,
/// named Simple / Confused / Clever / Wise Dog). FINALISE-PETS: a captured dog's nature is ROLLED over
/// Cowardly (3) / Rational (5) / Brave (10), the three natures the farm donation scores
/// (<see cref="LelantineFarmPointsPolicy"/>); the dog's kind only shifts the odds (GENERATED weights, see
/// <see cref="NatureWeights"/>). Before 2026-11-05 the kind fixed the aptitude (1/5/5/8), which is why
/// only "Rational" dogs were ever seen.
/// </summary>
internal static class LelantineFarmDogPolicy
{
    public const uint EggItemId = 10159;

    public static IEnumerable<string> TemplateKeys { get; } =
        new[] { "A", "B", "C", "D" }.SelectMany(kind => new[] { "male", "famale" }, (kind, sex) => $"A_normal{kind}_{sex}_001").ToArray();

    /// <summary>
    /// GENERATED capture chance per tool (the admin: wooden about 90%). The item texts only rank the tools
    /// (slightly / fair / quite a bit / substantially higher, Tuck Net nearly 100%), so the other
    /// figures are an even ladder above the wooden one. A miss uses the tool up and the dog runs off.
    /// </summary>
    public static double CaptureChance(int netItemId) => netItemId switch
    {
        10080 => 0.90,
        10081 => 0.93,
        10082 => 0.96,
        10083 => 0.98,
        _ => 1.0
    };

    /// <summary>
    /// GENERATED nature odds in basis points (total 10,000) per dog kind: Simple (Normal tier) 60% Cowardly /
    /// 32% Rational / 8% Brave; Confused and Clever (Enhanced) 45 / 40 / 15; Wise (Mythic) 30 / 45 / 25. No
    /// IGG, wiki or client source gives odds; the guide only says a higher-grade egg is worth more.
    /// </summary>
    public static IReadOnlyList<(short Aptitude, int BasisPoints)> NatureWeights(MedusaEncounterDifficulty tier) =>
        tier switch
        {
            MedusaEncounterDifficulty.Normal =>
                [(LelantineFarmPointsPolicy.CowardlyEggAptitude, 6_000), (LelantineFarmPointsPolicy.RationalEggAptitude, 3_200),
                 (LelantineFarmPointsPolicy.BraveEggAptitude, 800)],
            MedusaEncounterDifficulty.Enhanced =>
                [(LelantineFarmPointsPolicy.CowardlyEggAptitude, 4_500), (LelantineFarmPointsPolicy.RationalEggAptitude, 4_000),
                 (LelantineFarmPointsPolicy.BraveEggAptitude, 1_500)],
            MedusaEncounterDifficulty.Mythic =>
                [(LelantineFarmPointsPolicy.CowardlyEggAptitude, 3_000), (LelantineFarmPointsPolicy.RationalEggAptitude, 4_500),
                 (LelantineFarmPointsPolicy.BraveEggAptitude, 2_500)],
            _ => throw new InvalidDataException("The Lelantine dog tier is not published.")
        };

    /// <summary>The nature a roll in [0, 10,000) gives for this dog kind.</summary>
    public static short RollNature(MedusaEncounterDifficulty tier, int roll)
    {
        if (roll is < 0 or >= 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(roll));
        }

        foreach (var (aptitude, basisPoints) in NatureWeights(tier))
        {
            if (roll < basisPoints)
            {
                return aptitude;
            }
            roll -= basisPoints;
        }

        throw new InvalidDataException("The Lelantine dog nature weights do not cover 10,000 basis points.");
    }

    public static bool TryGetTier(string templateKey, out MedusaEncounterDifficulty tier)
    {
        tier = default;
        const string prefix = "A_normal";
        if (!templateKey.StartsWith(prefix, StringComparison.Ordinal) || templateKey.Length <= prefix.Length ||
            !(templateKey.EndsWith("_male_001", StringComparison.Ordinal) ||
              templateKey.EndsWith("_famale_001", StringComparison.Ordinal)))
        {
            return false;
        }
        switch (templateKey[prefix.Length])
        {
            case 'A': tier = MedusaEncounterDifficulty.Normal; return true;
            case 'B':
            case 'C': tier = MedusaEncounterDifficulty.Enhanced; return true;
            case 'D': tier = MedusaEncounterDifficulty.Mythic; return true;
            default: return false;
        }
    }
}
