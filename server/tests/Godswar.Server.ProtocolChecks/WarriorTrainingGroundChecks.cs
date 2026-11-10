using Godswar.Server.Application.Gates;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// WARRIOR (the admin F-51): the Warriors' Training Ground (Instance Caller root 13) -
/// the client's page, the four ground buttons, maps / scenes / level floors, the
/// switch, daily kinds 21-24, the wiki's scoring (1 / 5 / 10, 200 to clear, 20
/// minutes) and both reward tables, respawn, and that Heracles (map 210) is unchanged.
/// </summary>
internal static class WarriorTrainingGroundChecks
{
    public const string CheckName =
        "Warriors' Training Ground: root 13 page, four grounds, 200 points, wiki rewards, switch";

    public static Task RunAsync()
    {
        CheckPageAndEntry();
        CheckSwitchAndDaily();
        CheckScoring();
        CheckRewards();
        CheckContent();
        CheckHeraclesUnchanged();
        return Task.CompletedTask;
    }

    private static int[] ArgumentsFor(int first)
    {
        var arguments = Enumerable.Repeat(-1, InstanceCallerProtocol.FunctionArgumentCount).ToArray();
        arguments[0] = first;
        return arguments;
    }

    private static void CheckPageAndEntry()
    {
        var empty = Enumerable.Repeat(-1, InstanceCallerProtocol.FunctionArgumentCount).ToArray();
        Check.True(
            InstanceCallerProtocol.TryGetPage(InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.WarriorTrialRootSubId, empty, out var root, out var page) &&
            root == 13 && page.SequenceEqual(new[] { 203, 200, 201, 202, 301 }),
            "root 13 answers the client's own page: text 203, buttons 200/201/202/301");

        (int Page, byte Map, int Scene, int Level, InstanceCallerEntryKind Kind)[] expected =
        [
            (200, 201, 219, 40, InstanceCallerEntryKind.WarriorTrainingGround1),
            (201, 202, 220, 60, InstanceCallerEntryKind.WarriorTrainingGround2),
            (202, 203, 221, 80, InstanceCallerEntryKind.WarriorTrainingGround3),
            (301, 206, 225, 91, InstanceCallerEntryKind.WarriorTrainingGround4)
        ];
        foreach (var row in expected)
        {
            var resolved = InstanceCallerProtocol.TryResolveWarriorTrialEntry(
                InstanceCallerProtocol.DialogIndex, 13, ArgumentsFor(row.Page), out var ground);
            var destination = resolved ? ground.Destination : null;
            Check.True(
                resolved && destination is not null &&
                destination.Kind == row.Kind && destination.TargetMapId == row.Map &&
                destination.ClientSceneId == row.Scene && destination.MinimumLevel == row.Level &&
                destination.MaximumLevel == 200 && destination.MinimumPartySize == 1 &&
                destination.RequiredPartySize is null &&
                destination.TargetX == 176f && destination.TargetZ == 3f &&
                InstanceCallerProtocol.UsesPerMemberEntryWindow(row.Kind) &&
                InstanceCallerProtocol.TryResolveInvitedInstance(row.Scene, out var invited) &&
                invited.Kind == row.Kind && invited.TargetMapId == row.Map,
                $"button {row.Page} -> map {row.Map}, scene {row.Scene}, level {row.Level}+, per-member Enter window");
        }

        var extraPath = ArgumentsFor(200);
        extraPath[1] = 5;
        Check.True(
            !InstanceCallerProtocol.TryResolveWarriorTrialEntry(InstanceCallerProtocol.DialogIndex, 17, ArgumentsFor(200), out _) &&
            !InstanceCallerProtocol.TryResolveWarriorTrialEntry(InstanceCallerProtocol.DialogIndex, 13, ArgumentsFor(203), out _) &&
            !InstanceCallerProtocol.TryResolveWarriorTrialEntry(InstanceCallerProtocol.DialogIndex, 13, ArgumentsFor(231), out _) &&
            !InstanceCallerProtocol.TryResolveWarriorTrialEntry(InstanceCallerProtocol.DialogIndex, 13, extraPath, out _) &&
            !InstanceCallerProtocol.TryResolveWarriorTrialEntry(InstanceCallerProtocol.DialogIndex + 1, 13, ArgumentsFor(200), out _),
            "only root 13 with exactly one ground button is an entry (not 203, not 231, no extra path)");
        Check.True(
            !InstanceCallerProtocol.TryResolveHeraclesTrialEntry(InstanceCallerProtocol.DialogIndex, ArgumentsFor(200), out _, out _, out _) &&
            !InstanceCallerProtocol.TryGetLevelGateResult(InstanceCallerProtocol.DialogIndex, ArgumentsFor(301), out _) &&
            !InstanceCallerProtocol.TryResolveEntry(InstanceCallerProtocol.DialogIndex, 13, ArgumentsFor(200), out _),
            "the ground buttons never reach the Heracles, level-gate or Atlantis/Wonderland paths");
    }

    private static void CheckSwitchAndDaily()
    {
        var path = Enumerable.Repeat(-1, InstanceCallerProtocol.FunctionArgumentCount).ToArray();
        Check.True(
            ServerRateCatalog.Definitions.Single(static d => d.Key == ServerRateKeys.WarriorTrainingEntry).DefaultValue == 0m &&
            ServerRatesSnapshot.Default.Absolute(ServerRateKeys.WarriorTrainingEntry) == 0m,
            "instance.warrior_training_entry defaults to 0 (closed)");
        Check.True(
            InstanceCallerProtocol.ResolveClosedInstance(InstanceCallerProtocol.DialogIndex, 13, path, true, true, false) ==
                InstanceCallerProtocol.ClosedInstance.WarriorTraining &&
            InstanceCallerProtocol.ResolveClosedInstance(InstanceCallerProtocol.DialogIndex, 13, ArgumentsFor(202), true, true, false) ==
                InstanceCallerProtocol.ClosedInstance.WarriorTraining &&
            InstanceCallerProtocol.ResolveClosedInstance(InstanceCallerProtocol.DialogIndex, 13, ArgumentsFor(202), false, false, true) ==
                InstanceCallerProtocol.ClosedInstance.None &&
            InstanceCallerProtocol.ResolveClosedInstance(InstanceCallerProtocol.DialogIndex, 17, path, true, false, true) ==
                InstanceCallerProtocol.ClosedInstance.TrainingGround &&
            InstanceCallerProtocol.ResolveClosedInstance(InstanceCallerProtocol.DialogIndex, 17, path, true, true, false) ==
                InstanceCallerProtocol.ClosedInstance.None,
            "root 13 follows only its own switch; root 17 only the Heracles switch");
        Check.True(
            InstanceEntrySwitches.WarriorTrainingClosedNote.Length <= 63,
            "the closed note fits one 63-byte note field");

        foreach (var ground in WarriorTrainingGroundPolicy.Definitions)
        {
            Check.True(
                (int)ground.EntryKind == 20 + ground.Ground &&
                Enum.IsDefined(ground.EntryKind) &&
                TrainingGroundPolicy.IsTrainingGroundKind(ground.EntryKind) &&
                LegacyInstanceDailyEntryPolicy.GetDefaultFreeEntryLimit(ground.EntryKind) == 1 &&
                LegacyInstanceDailyEntryPolicy.GetDefaultPaidRetryLimit(ground.EntryKind) == 0 &&
                LegacyInstanceDailyEntryPolicy.GetDefaultDailyEntryLimit(ground.EntryKind) == 1 &&
                LegacyInstanceAdmissionPolicy.GateKey(ground.EntryKind) == $"instance.warrior_training_{ground.Ground}" &&
                GateCatalog.All.Any(gate => gate.Key == ground.GateKey && gate.CompiledMinimum == ground.MinimumLevel && gate.CompiledMaximum == 200),
                $"ground {ground.Ground}: daily kind {20 + ground.Ground}, 1 free / 0 paid, its own gate row");
        }
        Check.True(
            PostgresTrainingGroundStore.SchemaSql.Contains("instance_kind >= 21 AND instance_kind <= 24", StringComparison.Ordinal) &&
            PostgresTrainingGroundStore.SchemaSql.Contains("VALUES (21, 1, 0), (22, 1, 0), (23, 1, 0), (24, 1, 0)", StringComparison.Ordinal) &&
            PostgresTrainingGroundStore.SchemaSql.Contains("'instance_kind >= 21') = 0", StringComparison.Ordinal) &&
            !PostgresTrainingGroundStore.SchemaSql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase),
            "first-use DDL widens the shared kind checks for 21-24 (idempotent) and seeds 1 free / 0 paid");
    }

    private static void CheckScoring()
    {
        Check.True(
            WarriorTrainingGroundPolicy.ScoreFor("A_normalE_boar_006") == 1 &&
            WarriorTrainingGroundPolicy.ScoreFor("B_normalE_wasp_005") == 1 &&
            WarriorTrainingGroundPolicy.ScoreFor("A_eliteE_snake_008") == 5 &&
            WarriorTrainingGroundPolicy.ScoreFor("A_eliteEA_dragon_004") == 5 &&
            WarriorTrainingGroundPolicy.ScoreFor("A_bossE_lion_002") == 10 &&
            WarriorTrainingGroundPolicy.ScoreFor("B_bossA_dracoladon_003") == 10 &&
            WarriorTrainingGroundPolicy.ScoreFor("c_eliteA_SpartaTower_001") == 0 &&
            WarriorTrainingGroundPolicy.ScoreFor("tw000") == 0 &&
            WarriorTrainingGroundPolicy.ScoreFor(string.Empty) == 0,
            "wiki: regular 1, elite 5, boss 10; tower and crystals score nothing");
        Check.True(
            WarriorTrainingGroundPolicy.IsMagmaScorpion("A_bossE_scorpion_008") &&
            WarriorTrainingGroundPolicy.IsMagmaScorpion("B_bossE_scorpion_008") &&
            !WarriorTrainingGroundPolicy.IsMagmaScorpion("A_normalE_scorpion_002"),
            "the Magma Scorpion is the grounds' boss-rank scorpion (zh 魔岩毒蝎)");

        var at = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        Check.True(TrainingGroundPolicy.TryGetGround(202, out var ground) && ground.TargetScore == 200 &&
            ground.TimeLimit == TimeSpan.FromMinutes(20) && ground.ClientSceneId == 220,
            "map 202 is ground 2: 200 points, 20 minutes, scene 220");
        var progress = new TrainingGroundRunProgress(ground);
        Check.Equal((int)TrainingGroundKillOutcome.Scored,
            (int)progress.Record(49201, 1, "A_normalE_boar_006", at), "a regular monster scores");
        Check.Equal((int)TrainingGroundKillOutcome.Ignored,
            (int)progress.Record(49201, 1, "A_normalE_boar_006", at), "a replayed kill is ignored");
        progress.Record(49283, 1, "A_bossE_scorpion_008", at);
        progress.Record(49284, 1, "A_bossE_dryad_001_000", at);
        for (var elite = 0; elite < 10; elite++)
        {
            progress.Record(49273u + (uint)elite, 1, "A_eliteE_wasp_001", at);
        }
        Check.Equal(71, progress.Score, "1 + 2 bosses (20) + 10 elites (50) = 71");
        var last = TrainingGroundKillOutcome.Ignored;
        for (var kill = 0; kill < 129; kill++)
        {
            last = progress.Record(49202u + (uint)(kill % 70), 10u + (uint)kill, "A_normalE_lion_004", at.AddMinutes(14));
        }
        Check.True(last == TrainingGroundKillOutcome.Completed && progress.Score == 200 &&
            progress.CompletedAt == at.AddMinutes(14) && progress.Elites == 12,
            "the 200th point completes the run (bosses and elites tallied as elites)");
        Check.Equal((int)TrainingGroundKillOutcome.Ignored,
            (int)progress.Record(49284, 2, "A_bossE_dryad_001_000", at.AddMinutes(15)), "nothing scores after the clear");
    }

    private static void CheckRewards()
    {
        static bool Is(TrainingGroundReward reward, int exp, int tp) =>
            reward.Experience == exp && reward.TalentPoints == tp;

        int[][] experience =
        [
            [10_000, 50_000, 70_000, 80_000, 90_000, 110_000],
            [30_000, 108_000, 144_000, 192_000, 216_000, 228_000],
            [60_000, 240_000, 288_000, 312_000, 360_000, 384_000],
            [65_000, 400_000, 480_000, 560_000, 640_000, 720_000]
        ];
        int[][] talent = [[2, 3, 6, 9, 12, 16], [2, 3, 6, 9, 12, 16], [2, 3, 6, 9, 12, 16], [3, 8, 12, 18, 22, 30]];
        for (var ground = 1; ground <= 4; ground++)
        {
            var e = experience[ground - 1];
            var t = talent[ground - 1];
            TrainingGroundReward R(int score, TimeSpan? cleared = null) =>
                WarriorTrainingGroundPolicy.RewardFor(ground, score, cleared);
            Check.True(
                Is(R(0), e[0], t[0]) && Is(R(79), e[0], t[0]) &&
                Is(R(80), e[1], t[1]) && Is(R(119), e[1], t[1]) &&
                Is(R(120), e[2], t[2]) && Is(R(160), e[3], t[3]) && Is(R(199), e[3], t[3]) &&
                Is(R(200, TimeSpan.FromMinutes(19)), e[4], t[4]) &&
                Is(R(200, TimeSpan.FromMinutes(15)), e[5], t[5]) &&
                Is(R(200, TimeSpan.FromMinutes(3)), e[5], t[5]) &&
                Is(R(200), e[3], t[3]),
                $"ground {ground}: the wiki's table (0/80/120/160, 200 within 20 / 15 minutes)");
            Check.True(TrainingGroundPolicy.TryGetGround(WarriorTrainingGroundPolicy.Definitions[ground - 1].MapId, out var definition) &&
                Is(definition.Reward(120, null), e[2], t[2]),
                $"ground {ground}'s definition pays its own column");
        }
        Check.True(
            WarriorTrainingGroundPolicy.ResolveTimeLimit(null) == TimeSpan.FromMinutes(20) &&
            WarriorTrainingGroundPolicy.ResolveTimeLimit("30") == TimeSpan.FromMinutes(20) &&
            WarriorTrainingGroundPolicy.ResolveTimeLimit("1300") == TimeSpan.FromMinutes(20) &&
            WarriorTrainingGroundPolicy.ResolveTimeLimit("90") == TimeSpan.FromSeconds(90) &&
            WarriorTrainingGroundPolicy.FastClear == TimeSpan.FromMinutes(15),
            "20 minutes (rig override 60-1200 s only), fast tier 15 minutes");
    }

    private static void CheckContent()
    {
        var ordinary = TimeSpan.FromSeconds(10);
        foreach (var ground in WarriorTrainingGroundPolicy.Definitions)
        {
            Check.True(
                DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(ground.MapId) &&
                DynamicDungeonContentMapPolicy.IsWarriorTrainingGroundMap(ground.MapId) &&
                TrainingGroundPolicy.IsTrainingGroundMap(ground.MapId) &&
                GameSessionRegistry.MonsterRespawnPolicyForMap(ground.MapId) == MonsterRespawnPolicy.Timed,
                $"map {ground.MapId} is an exact-instance Training Ground map with timed respawn");
            Check.True(ground.BossKeys.Contains(ground.GroundBossKey) &&
                ground.BossKeys.Any(WarriorTrainingGroundPolicy.IsMagmaScorpion) &&
                ground.BossKeys.All(static key => WarriorTrainingGroundPolicy.ScoreFor(key) == 10) &&
                ground.EliteKeys.Length == 8 &&
                ground.EliteKeys.All(static key => WarriorTrainingGroundPolicy.ScoreFor(key) == 5),
                $"ground {ground.Ground}: the wiki boss and the Magma Scorpion are bosses, 8 elite templates");
            foreach (var key in ground.EliteKeys.Concat(ground.BossKeys))
            {
                Check.True(MonsterRespawnPolicyRules.ResolveRespawnInterval(
                        WorldBossCatalog.Empty, ground.MapId, key, ordinary) == TimeSpan.FromHours(1),
                    $"{key} on {ground.MapId} appears once per run");
            }
            Check.True(MonsterRespawnPolicyRules.ResolveRespawnInterval(
                    WorldBossCatalog.Empty, ground.MapId, "A_normalE_boar_006", ordinary) == ordinary,
                $"regular monsters on {ground.MapId} keep the ordinary respawn");
        }
        // The GM spawn layer only admits a map's own catalog templates: every key the
        // policy (and the generated layout) uses must be a client template of that map.
        foreach (var ground in WarriorTrainingGroundPolicy.Definitions)
        {
            var own = MonsterTemplateSeeds.Monsters
                .Where(seed => seed.SourceMapId == ground.MapId)
                .ToDictionary(static seed => seed.TemplateKey, StringComparer.Ordinal);
            Check.True(
                ground.BossKeys.All(key => own.TryGetValue(key, out var seed) && seed.IsBoss) &&
                ground.EliteKeys.All(key => own.TryGetValue(key, out var seed) && seed.IsElite) &&
                own.Count(static pair => pair.Value.Rank == "normal") >= 24,
                $"ground {ground.Ground}: every boss / elite key is a template of map {ground.MapId} with that rank");
        }
        // Spawn object ids never overlap the world runtime-spawn pool or the other
        // reserved generated ranges, and stay inside the monster id registry.
        (uint First, uint Last, string Name)[] reserved =
        [
            (WorldRuntimeSpawnOverlay.FirstObjectId, WorldRuntimeSpawnOverlay.LastObjectId, "runtime spawn pool"),
            (45_101, 45_524, "Troy monsters (PR #110)"),
            (47_930, 47_938, "Troy NPCs (PR #110)"),
            (46_001, 46_075, "Heracles spawns"),
            (49_901, 49_912, "pet capture spawns"),
            (WorldObjectIds.FirstMedusaMonsterObjectId, 40_999, "Medusa"),
            (42_000, 42_999, "Atlantis waves")
        ];
        for (var g = 1; g <= 4; g++)
        {
            var (first, last) = WarriorTrainingGroundPolicy.ObjectIdRange(g);
            Check.True(
                first >= WarriorTrainingGroundPolicy.FirstObjectId && last <= WarriorTrainingGroundPolicy.LastObjectId &&
                last <= WorldObjectIds.LastMonsterObjectId && first >= WorldObjectIds.FirstMonsterObjectId &&
                !WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(first) &&
                !WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(last) &&
                reserved.All(r => last < r.First || first > r.Last),
                $"ground {g} spawn ids {first}-{last} overlap no reserved range ({string.Join(", ", reserved.Select(static r => r.Name))})");
        }
        Check.True(
            WarriorTrainingGroundPolicy.Definitions.Select(static g => g.GroundBossKey).SequenceEqual(new[]
            {
                "A_bossE_lion_002", "A_bossE_dryad_001_000", "A_bossE_bull_001", "B_bossA_dracoladon_003"
            }),
            "the wiki's bosses: Nemean Lion, Ceryneian Hind, Cretan Bull, Lernaean Hydra");
    }

    private static void CheckHeraclesUnchanged()
    {
        Check.True(
            TrainingGroundPolicy.TryGetGround(210, out var heracles) &&
            ReferenceEquals(heracles, TrainingGroundPolicy.Heracles) &&
            heracles.TargetScore == 300 && heracles.ClientSceneId == 256 &&
            heracles.EntryKind == InstanceCallerEntryKind.TrainingGround &&
            heracles.TimeLimit == TrainingGroundPolicy.TimeLimit &&
            heracles.Reward(300, TimeSpan.FromMinutes(20)).Experience == 384_000 &&
            heracles.Score("A_elite_spider_006") == 10 && heracles.Score("A_bossE_lion_002") == 0 &&
            !TrainingGroundPolicy.TryGetGround(204, out _) &&
            !TrainingGroundPolicy.IsTrainingGroundMap(207),
            "Heracles (210) keeps the admin's 300 points / 30 minutes / own table; other maps are not grounds");
        Check.True(new TrainingGroundRunProgress().Record(1, 1, "A_bossE_lion_002", DateTimeOffset.UtcNow) ==
                TrainingGroundKillOutcome.Ignored,
            "a Warriors' boss key never scores on Heracles' default progress");
    }
}
