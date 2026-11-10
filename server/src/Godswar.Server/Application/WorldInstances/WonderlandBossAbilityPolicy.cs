namespace Godswar.Server.Application.WorldInstances;

internal enum WonderlandAbilityShape : byte { Single, Circle, Frontal }

internal sealed record WonderlandBossAbility(
    string Key, uint NativeSkillId, bool Magical, decimal DamageMultiplier,
    TimeSpan Cooldown, TimeSpan Windup, WonderlandAbilityShape Shape,
    float Radius = 6f, int HitCount = 1, int PhysicalDefensePenaltyBasisPoints = 0,
    int PenaltySeconds = 0);

/// <summary>Retained authored exceptions; ordinary Wonderland attacks use captured native skills.</summary>
internal static class WonderlandBossAbilityPolicy
{
    private static WonderlandBossAbility Ability(string key, uint skillId, bool magical, decimal multiplier,
        double cooldown, WonderlandAbilityShape shape = WonderlandAbilityShape.Circle,
        double windup = 1.2, float radius = 6f, int hits = 1, int penalty = 0, int seconds = 0) =>
        new(key, skillId, magical, multiplier, TimeSpan.FromSeconds(cooldown), TimeSpan.FromSeconds(windup),
            shape, radius, hits, penalty, seconds);

    public static IReadOnlyList<WonderlandBossAbility> For(string mechanicKey) => mechanicKey switch
    {
        // The complete September 13 run has no boss-sourced 10040 casts.
        // Its skills are the primary 10046 in each ordinary attack pair.
        // The user explicitly keeps our island-three boss skill instead.
        "rooster" => [Ability("Fire Blast", 580, true, 2.5m, 4)],
        _ => []
    };

    public static WonderlandBossAbility AtlasDeathBlast { get; } =
        Ability("Atlas Final Fire Blast", 2179, true, 3m, 0, windup: 1.5);

    public static WonderlandBossAbility ReflectionMarker { get; } =
        Ability("Rock Reflection", 2000, false, 1m, 0, WonderlandAbilityShape.Single, windup: 0);

    public static WonderlandBossAbility TerrainFire { get; } =
        Ability("Ground Fire", 580, true, 2.5m, 3, windup: 1.5, radius: 5);

    public static int AccuracyBasisPoints(int hit, int dodge) =>
        checked((int)Math.Clamp(9000L + ((long)Math.Max(0, hit) - Math.Max(0, dodge)) / 2, 500, 9800));

    public static uint ReflectionDamage(uint before, uint after) =>
        before > after ? (before - after) / 10 : 0;

    public static int ScaleRating(int value, int multiplier) =>
        checked((int)Math.Clamp((long)Math.Max(0, value) * Math.Max(1, multiplier), 0, int.MaxValue));

    public static bool InShape(WonderlandBossAbility ability, float originX, float originZ,
        float aimX, float aimZ, float targetX, float targetZ)
    {
        var dx = targetX - originX;
        var dz = targetZ - originZ;
        var distanceSquared = dx * dx + dz * dz;
        if (!float.IsFinite(distanceSquared) || distanceSquared > ability.Radius * ability.Radius) return false;
        if (ability.Shape != WonderlandAbilityShape.Frontal || distanceSquared == 0) return true;
        var ax = aimX - originX;
        var az = aimZ - originZ;
        var aimSquared = ax * ax + az * az;
        var dot = ax * dx + az * dz;
        return aimSquared > 0 && dot >= 0 && 2 * dot * dot >= aimSquared * distanceSquared;
    }
}
