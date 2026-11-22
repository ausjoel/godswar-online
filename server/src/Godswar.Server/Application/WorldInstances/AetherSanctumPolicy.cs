namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// B22 G2 (the admin d36): the Lv170-200 shared map "Aether Sanctum" (GENERATED name), its own content map.
/// Server map 70, client scene 257 (client MapIdToNameConfig MapID70 = Aether_Sanctum,257, written by
/// tools/b22-g2/gen_g2_client.py). Its monsters drop Aether Crests (MedalLootRule).
/// B23-S (the admin d82, d92): the map is a copy of Bay Under Attack (Salame2, map 209) instead of Olympus, still a
/// shared map (no party run, no timer, no daily entry, none of the harbor run rules: those are keyed to maps
/// 208/209). Its monsters have their own template keys (Salame2's with "SAQ" -> "AS"), so they use their own
/// loot tables (copies of Olympus' Lv190-200 tables) and not the Lv50-90 harbor gear. Bosses: AetherSanctumBossScale.
/// </summary>
internal static class AetherSanctumPolicy
{
    public const byte MapId = 70;
    public const int ClientSceneId = 257;
    public const string SceneKey = "Aether_Sanctum";
    public const string DisplayName = "Aether Sanctum";
    public const int MinimumLevel = 170;
    public const int MaximumLevel = 200;

    /// <summary>
    /// Arrival (button 1105, tools/b22-g2/gen_sanctum_sql.py ARRIVAL; the exit teleporter stands beside it).
    /// Not Bay's own arrival (118, 0): that cell is 12.5 from the first bandit group, inside the 14-unit
    /// detection radius, so Lv185 bandits attacked every arriving Lv170 at once on the rig. (130, 5) is reachable
    /// from (118, 0) on the Salame walk plane, has 21 units of clearance and is 23.7 from the nearest bandit.
    /// </summary>
    public const float ArrivalX = 130f;
    public const float ArrivalZ = 5f;

    /// <summary>Bloody Skeleton and Christina (GENERATED Sanctum copies): respawn after 60 minutes.</summary>
    public static readonly string[] BossKeys = ["B_bossAS_skeleton_001", "B_bossAS_mage_018"];

    /// <summary>The four elite templates: respawn after 15 minutes.</summary>
    public static readonly string[] EliteKeys =
    [
        "B_eliteAS_persiawarrior_001", "B_eliteAS_persiawarrior_002", "B_eliteAS_persiawarrior_003",
        "B_eliteAS_persiawarrior_005"
    ];
}
