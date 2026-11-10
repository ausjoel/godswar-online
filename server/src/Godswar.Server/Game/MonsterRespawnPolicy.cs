using Godswar.Server.State;

namespace Godswar.Server.Game;

internal enum MonsterRespawnPolicy
{
    Timed = 0,
    Never = 1
}

internal static class MonsterRespawnPolicyRules
{
    /// <summary>Thermopylae, the only map with an authored field elite.</summary>
    private const short ThermopylaeMapId = 8;

    private const string ThermopylaeEliteTemplateKey =
        "B_eliteB_stymphalianbird_001";

    /// <summary>
    /// Authored cadences that differ from the runtime-wide ordinary respawn
    /// delay. World bosses resolve through <see cref="WorldBossCatalog"/>
    /// instead, so this table only carries field encounters: the runtime delay
    /// stays a single value and only the monsters listed here are special.
    /// </summary>
    private static readonly Dictionary<(short MapId, string TemplateKey), TimeSpan>
        AuthoredRespawnIntervals = BuildAuthoredRespawnIntervals();

    private static Dictionary<(short MapId, string TemplateKey), TimeSpan> BuildAuthoredRespawnIntervals()
    {
        var intervals = new Dictionary<(short MapId, string TemplateKey), TimeSpan>()
        {
            // Thermopylae holds exactly one field elite. An hour keeps it a
            // deliberate target rather than a continuous farm, and because a
            // monster only respawns after its own death the area can never
            // hold two at once.
            [(ThermopylaeMapId, ThermopylaeEliteTemplateKey)] =
                TimeSpan.FromHours(1)
        };
        // Lelantine Farm dogs come back a minute after they are caught or killed.
        foreach (var template in Godswar.Server.Application.WorldInstances.LelantineFarmDogPolicy.TemplateKeys)
            intervals[(Godswar.Server.Domain.World.Content.LelantineFarmProtocol.MapId, template)] =
                TimeSpan.FromSeconds(60);
        // Farm elites (10 points a kill) wait two minutes; Cerberus (1,000 points) waits the length of one
        // 45-minute round, so it can be scored once per battle. GENERATED, no reference value exists.
        foreach (var elite in new[]
                 {
                     "A_elite_wolf_004", "B_eliteA_wolf_004", "B_eliteB_wolf_004", "A_elite_wolf_005", "B_elite_wolf_005"
                 })
            intervals[(Godswar.Server.Domain.World.Content.LelantineFarmProtocol.MapId, elite)] = TimeSpan.FromMinutes(2);
        intervals[(Godswar.Server.Domain.World.Content.LelantineFarmProtocol.MapId, "B_boss_wolf_006")] =
            TimeSpan.FromMinutes(45);
        // BATTLEFIELD: the Pindus Dragon (map 38) returns 5 minutes after it is brought down (IGG guide acid=123).
        intervals[(Godswar.Server.Application.WorldInstances.BattlefieldRewardPolicy.PindusMapId,
            Godswar.Server.Application.WorldInstances.BattlefieldRewardPolicy.PindusDragonTemplateKey)] =
            TimeSpan.FromMinutes(5);
        // TRAINING: Training Ground elites appear once per run (normals keep the ordinary delay).
        foreach (var elite in Godswar.Server.Application.WorldInstances.TrainingGroundPolicy.Templates.Values
                     .Where(static template => template.IsElite))
            intervals[(Godswar.Server.Application.WorldInstances.TrainingGroundPolicy.MapId, elite.TemplateKey)] =
                Godswar.Server.Application.WorldInstances.TrainingGroundPolicy.EliteRespawnInterval;
        // WARRIOR (F-51): the Warriors' grounds' elites and bosses appear once per run (GENERATED).
        foreach (var ground in Godswar.Server.Application.WorldInstances.WarriorTrainingGroundPolicy.Definitions)
            foreach (var key in ground.EliteKeys.Concat(ground.BossKeys))
                intervals[(ground.MapId, key)] =
                    Godswar.Server.Application.WorldInstances.WarriorTrainingGroundPolicy.OncePerRunRespawnInterval;
        // FINALISE2 F-19: the four Mycenae big bosses. Listed here so the per-map/tier windows never apply to them;
        // the kill path draws a fresh 30-60 minute delay (MycenaeBossPolicy), this value is only the hydration default.
        foreach (var boss in Godswar.Server.Application.North.MycenaeBossPolicy.TemplateKeys) // FINALISE2
            intervals[(Godswar.Server.Application.North.MycenaeBossPolicy.MapId, boss)] = TimeSpan.FromMinutes(45); // FINALISE2
        return intervals;
    }

    public static void Validate(MonsterRespawnPolicy policy)
    {
        if (policy is not (MonsterRespawnPolicy.Timed or MonsterRespawnPolicy.Never))
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy,
                "Unsupported monster respawn policy.");
        }
    }

    public static TimeSpan? ResolveOrdinaryDelay(
        MonsterRespawnPolicy policy,
        TimeSpan corpseDespawnDelay,
        TimeSpan? configuredRespawnDelay)
    {
        Validate(policy);
        if (policy == MonsterRespawnPolicy.Never)
        {
            if (configuredRespawnDelay is not null)
            {
                throw new ArgumentException(
                    "A never-respawn runtime cannot also define a respawn delay.",
                    nameof(configuredRespawnDelay));
            }

            return null;
        }

        var delay = configuredRespawnDelay ?? MonsterMapRuntime.DefaultRespawnDelay;
        if (delay <= corpseDespawnDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredRespawnDelay),
                "Monster respawn delay must be later than corpse despawn.");
        }

        return delay;
    }

    /// <summary>
    /// Resolves the cadence a killed monster waits before it reappears: a
    /// selected world boss keeps its scheduled interval, an authored field
    /// encounter uses its own, and everything else uses the runtime-wide delay.
    /// </summary>
    public static TimeSpan ResolveRespawnInterval(
        WorldBossCatalog worldBossCatalog,
        short mapId,
        string templateKey,
        TimeSpan ordinaryRespawnInterval)
    {
        ArgumentNullException.ThrowIfNull(worldBossCatalog);
        if (worldBossCatalog.IsWorldBoss(mapId, templateKey))
        {
            return worldBossCatalog.ResolveRespawnInterval(
                mapId,
                templateKey,
                ordinaryRespawnInterval);
        }

        // petcapture: wild baby pets wait their own interval, only while the switch is on.
        if (Godswar.Server.Application.WorldInstances.WildPetCapturePolicy.TryGetRespawnInterval(
                mapId, templateKey, out var wildPetInterval))
        {
            return wildPetInterval;
        }

        // PETNET: a caught (or killed) Treasure Land boss returns after six minutes while net capture is on.
        if (Godswar.Server.Application.WorldInstances.TreasureLandCapturePolicy.TryGetRespawnInterval(
                mapId, templateKey, out var treasureLandInterval))
        {
            return treasureLandInterval;
        }

        if (TryResolveTierDelay(mapId, templateKey, out var tierDelay)) // FINALISE
        {
            return tierDelay;
        }

        return AuthoredRespawnIntervals.TryGetValue(
            (mapId, templateKey),
            out var authoredInterval)
            ? authoredInterval
            : ordinaryRespawnInterval;
    }

    /// <summary>
    /// FINALISE: the per-map/tier random respawn window (respawn_tier_overrides). Authored cadences (Training
    /// Ground, Lelantine Farm, Pindus Dragon, Thermopylae elite) and wild baby pets keep their own; world bosses
    /// never reach here (the catalog answers first) and the boss tier is excluded anyway.
    /// </summary>
    public static bool TryResolveTierDelay(short mapId, string templateKey, out TimeSpan delay)
    {
        delay = default;
        if (AuthoredRespawnIntervals.ContainsKey((mapId, templateKey)) ||
            Godswar.Server.Application.WorldInstances.WildPetCapturePolicy.TryGetRespawnInterval(
                mapId, templateKey, out _) ||
            Godswar.Server.Application.WorldInstances.TreasureLandCapturePolicy.TryGetRespawnInterval(
                mapId, templateKey, out _)) // PETNET
        {
            return false;
        }

        return Godswar.Server.Application.Npc.MonsterTierOverrides.TryRespawnDelay(mapId, templateKey, out delay);
    }

    public static void RejectTimedWorldBossConfiguration(
        MonsterRespawnPolicy policy,
        byte mapId,
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        WorldBossRespawnState? activeWorldBossRespawn,
        WorldBossCatalog worldBossCatalog)
    {
        Validate(policy);
        if (policy != MonsterRespawnPolicy.Never)
        {
            return;
        }

        if (activeWorldBossRespawn is not null)
        {
            throw new ArgumentException(
                "A never-respawn runtime cannot restore a timed world-boss respawn.",
                nameof(activeWorldBossRespawn));
        }

        if (definitions.Any(definition =>
                worldBossCatalog.IsWorldBoss(mapId, definition.TemplateKey)))
        {
            throw new ArgumentException(
                "A configured world boss requires the timed respawn policy.",
                nameof(worldBossCatalog));
        }
    }
}
