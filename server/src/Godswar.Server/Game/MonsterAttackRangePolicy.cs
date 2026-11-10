using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

internal static class MonsterAttackRangePolicy
{
    public const float MeleeRange = 3f;
    public const float RangedRange = 9f;
    // Arrival and subsequent attack eligibility must agree. Float movement
    // can stop a fraction beyond the exact radius; inconsistent comparisons
    // restart chasing every other tick and continually reset the attack clock.
    public const double DistanceTolerance = 0.0001d;

    public static bool Contains(double distance, float attackRange) =>
        distance <= attackRange + DistanceTolerance;

    public static float Resolve(
        in MonsterCombatProfile profile,
        CapturedMonsterSpawn definition,
        bool passive = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (profile.AuthoredAttackRange is { } authoredRange)
        {
            if (passive && authoredRange == 0f)
            {
                return 0f;
            }
            if (authoredRange <= 0 || !float.IsFinite(authoredRange))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profile),
                    "Authored attack reach must be finite and positive.");
            }

            return authoredRange;
        }

        // AttackType primarily identifies the damage channel. The stock
        // Gorgon Archer is authored as physical despite using a bow.
        var isRangedRole = definition.DisplayName.Contains(
            "Archer",
            StringComparison.OrdinalIgnoreCase);
        // Type 3 (Special) on a melee body (Cursed Zone guards, Darkmist) is
        // listed as reach=melee in MonsterAttackPresentationTable: those
        // models only have bite/swing clips, so they must close to melee.
        var forcedMelee = MonsterAttackPresentationTable.ForcesMeleeReach(
            definition.TemplateKey);
        var authoredReach =
            !forcedMelee &&
            (profile.AttackKind != MonsterAttackDamageKind.Physical ||
             isRangedRole)
            ? RangedRange
            : MeleeRange;
        // The client stops center-to-center movement at the model's authored
        // collision range. A smaller server reach leaves large melee models
        // visibly touching the player while endlessly trying to close a gap
        // the client cannot represent.
        return Math.Max(authoredReach, profile.CollisionRange);
    }
}
