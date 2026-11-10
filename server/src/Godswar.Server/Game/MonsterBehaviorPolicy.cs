namespace Godswar.Server.Game;

/// <summary>Immutable movement boundaries scoped to one monster runtime.</summary>
internal sealed class MonsterBehaviorPolicy
{
    public static MonsterBehaviorPolicy Default { get; } = new(
        MonsterAggroPolicy.DetectionRadius,
        MonsterMapRuntime.CombatLeashRadius,
        MonsterMapRuntime.MaximumRoamRadius);

    public MonsterBehaviorPolicy(
        float aggroDetectionRadius,
        float combatLeashRadius,
        float maximumRoamRadius,
        bool stationary = false,
        TimeSpan? attackInterval = null,
        bool passive = false,
        MonsterNavigationGrid? navigationGrid = null)
    {
        if (passive)
        {
            if (!stationary || aggroDetectionRadius != 0f || maximumRoamRadius != 0f)
            {
                throw new ArgumentException(
                    "Passive actors must be stationary with zero aggro and roam radii.",
                    nameof(passive));
            }
        }
        else
        {
            ValidateRadius(aggroDetectionRadius, nameof(aggroDetectionRadius));
        }

        ValidateRadius(combatLeashRadius, nameof(combatLeashRadius));
        if (!stationary || maximumRoamRadius != 0f)
        {
            ValidateRadius(maximumRoamRadius, nameof(maximumRoamRadius));
        }

        if (stationary
                ? maximumRoamRadius != 0f
                : maximumRoamRadius < MonsterMapRuntime.MovementStep)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRoamRadius));
        }

        if (combatLeashRadius < aggroDetectionRadius + maximumRoamRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(combatLeashRadius),
                "The leash must contain targets acquired at the idle roam boundary.");
        }

        AggroDetectionRadius = aggroDetectionRadius;
        CombatLeashRadius = combatLeashRadius;
        MaximumRoamRadius = maximumRoamRadius;
        Stationary = stationary;
        Passive = passive;
        NavigationGrid = navigationGrid;
        AttackInterval = attackInterval ?? MonsterMapRuntime.AttackCooldown;
        if (AttackInterval <= TimeSpan.Zero ||
            AttackInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(attackInterval));
        }
    }

    public float AggroDetectionRadius { get; }
    public float CombatLeashRadius { get; }
    public float MaximumRoamRadius { get; }
    public bool Stationary { get; }
    public bool Passive { get; }
    public MonsterNavigationGrid? NavigationGrid { get; }
    public TimeSpan AttackInterval { get; }

    private static void ValidateRadius(float radius, string parameter)
    {
        if (radius <= 0f || !float.IsFinite(radius * radius))
        {
            throw new ArgumentOutOfRangeException(parameter);
        }
    }
}
