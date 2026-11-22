using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

// B23-S (the admin d82): the Aether Sanctum as a Bay Under Attack copy with very strong bosses (two hot sliders).
internal static class B23SanctumChecks
{
    public const string CheckName = "B23-S Aether Sanctum bosses: HP/damage sliders, own keys, respawn timers";

    public static Task RunAsync()
    {
        var hp = ServerRateCatalog.Get(AetherSanctumBossScale.HpKey);
        var damage = ServerRateCatalog.Get(AetherSanctumBossScale.DamageKey);
        Check.Equal(500m, hp.DefaultValue, "boss HP default 500%");
        Check.Equal(200m, damage.DefaultValue, "boss damage default 200%");
        Check.True(hp.MaximumValue >= 1000m && damage.MaximumValue >= 1000m, "sliders have headroom above the defaults");
        Check.Equal(AetherSanctumBossScale.Category, hp.Category, "own rates category");

        static CapturedMonsterSpawn Mob(short map, string key, uint level, uint health)
        {
            var packet = new byte[108];
            BitConverter.GetBytes(level).CopyTo(packet, 12);
            BitConverter.GetBytes(health).CopyTo(packet, 20);
            BitConverter.GetBytes(health).CopyTo(packet, 24);
            return new(map, "Aether_Sanctum", key, "mob", 49701, 0f, 0f, packet);
        }
        static uint Max(CapturedMonsterSpawn mob) => BitConverter.ToUInt32(mob.Packet, 24);
        static uint Cur(CapturedMonsterSpawn mob) => BitConverter.ToUInt32(mob.Packet, 20);

        var boss = Mob(70, "B_bossAS_mage_018", 200, 23_000_040);
        var scaled = AetherSanctumBossScale.NormalizeSpawn(boss, 500m);
        Check.True(Max(scaled) == 115_000_200u && Cur(scaled) == 115_000_200u, $"a Sanctum boss at 500%: 115,000,200 HP ({Max(scaled)})");
        Check.Equal(23_000_040u, Max(boss), "the published packet itself is not changed");
        Check.Equal(23_000_040u, Max(AetherSanctumBossScale.NormalizeSpawn(boss, 100m)), "100% = the normal Lv200 boss");
        var elite = Mob(70, "B_eliteAS_persiawarrior_001", 196, 8_000_000);
        Check.Equal(8_000_000u, Max(AetherSanctumBossScale.NormalizeSpawn(elite, 500m)), "Sanctum elites keep their HP");
        var olympusBoss = Mob(21, "C_boss_godsguard_010", 200, 23_000_040);
        Check.Equal(23_000_040u, Max(AetherSanctumBossScale.NormalizeSpawn(olympusBoss, 500m)), "bosses elsewhere keep their HP");

        Check.Equal(2000u, AetherSanctumBossScale.Scale(1000, 200m), "damage x2 at 200%");
        Check.Equal(1u, AetherSanctumBossScale.Scale(1, 10m), "a hit above 0 never drops below 1");
        Check.Equal(0u, AetherSanctumBossScale.Scale(0, 200m), "a miss stays a miss");
        Check.Equal(2_000u, AetherSanctumBossScale.ScaleDamage(70, "B_bossAS_skeleton_001", 1_000), "Sanctum boss hit x2 (default)");
        Check.Equal(1_000u, AetherSanctumBossScale.ScaleDamage(70, "B_normalAS_robber_001", 1_000), "Sanctum normal hit unchanged");
        Check.Equal(1_000u, AetherSanctumBossScale.ScaleDamage(209, "B_bossSAQ_mage_018", 1_000), "Bay Under Attack boss unchanged");

        // Own keys, not the harbor ones (their loot is Lv50-90 harbor gear; Christina's key ends a harbor run).
        Check.True(AetherSanctumPolicy.BossKeys.Concat(AetherSanctumPolicy.EliteKeys).All(static key =>
                key.Contains("AS_", StringComparison.Ordinal) && !HarborAttackContentPolicy.IsEndBoss(key)),
            "Sanctum keys are their own");
        // B22 B23-R: the way-out NPC (Mycenae's teleporter id 59721 placed on map 70) has no quest function there.
        Check.True(GameClientHandler.IsSanctumExitTransporter(70, TransporterProtocol.MycenaeNpcId), "Sanctum exit: quest marks suppressed on map 70");
        Check.True(!GameClientHandler.IsSanctumExitTransporter(6, TransporterProtocol.MycenaeNpcId), "Mycenae's own Altar Envoy keeps its quests");
        Check.True(!GameClientHandler.IsSanctumExitTransporter(70, 49754), "other map-70 objects are not affected");
        var ordinary = TimeSpan.FromMinutes(2);
        foreach (var key in AetherSanctumPolicy.BossKeys)
            Check.True(MonsterRespawnPolicyRules.ResolveRespawnInterval(WorldBossCatalog.Empty, 70, key, ordinary) == TimeSpan.FromHours(1),
                $"{key} respawns after 60 minutes");
        foreach (var key in AetherSanctumPolicy.EliteKeys)
            Check.True(MonsterRespawnPolicyRules.ResolveRespawnInterval(WorldBossCatalog.Empty, 70, key, ordinary) == TimeSpan.FromMinutes(15),
                $"{key} respawns after 15 minutes");
        Check.True(MonsterRespawnPolicyRules.ResolveRespawnInterval(WorldBossCatalog.Empty, 70, "B_normalAS_robber_001", ordinary) == ordinary,
            "normals keep the ordinary delay");
        Check.True(AetherSanctumPolicy.ArrivalX == 130f && AetherSanctumPolicy.ArrivalZ == 5f, "arrival out of the first bandits' detection radius");
        return Task.CompletedTask;
    }
}
