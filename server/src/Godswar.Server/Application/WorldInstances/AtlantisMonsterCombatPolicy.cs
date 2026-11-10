using Godswar.Server.Game;

namespace Godswar.Server.Application.WorldInstances;

internal static class AtlantisMonsterCombatPolicy
{
    public const float TriggerX = 19f;
    public const float TriggerZ = 53f;

    // The farthest authored member is 100.961 units from the trigger. This
    // detection radius includes its two-unit idle movement and a safety margin.
    // A larger leash lets the entire group reach the player before returning.
    public static MonsterBehaviorPolicy Behavior { get; } = new(112f, 128f, 2f);
}
