using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class AtlantisMonsterCombatChecks
{
    public const string CheckName =
        "Atlantis grouped aggro reach, bounded pursuit, and isolated Legacy/ECS settings";
    private static readonly DateTimeOffset Start =
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        CheckConfigurationValidation();
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            CheckTriggerAndPursuit(mode);
            CheckRoundedAttackBoundary(mode);
            CheckIdleGrouping(mode);
        }
        return Task.CompletedTask;
    }

    private static void CheckTriggerAndPursuit(MonsterRuntimeMode mode)
    {
        var runtime = CreateLiveWave(mode);
        var monsters = ScoringMonsters(runtime);
        Check.Equal(12, monsters.Length, "the live integration fixture exposes all twelve scored members");
        var target = new MonsterCombatTarget(101,
            AtlantisMonsterCombatPolicy.TriggerX,
            AtlantisMonsterCombatPolicy.TriggerZ, true);
        var behavior = AtlantisMonsterCombatPolicy.Behavior;
        Check.True(monsters.All(monster =>
                Distance(monster.X, monster.Z, target.X, target.Z) +
                behavior.MaximumRoamRadius < behavior.AggroDetectionRadius),
            "every offset member can detect the requested point even after idle roaming");

        runtime.Advance(Start, [target with { X = 171f, Z = 24f }]);
        Check.True(ScoringMonsters(runtime).All(monster =>
                monster.CombatPhase == MonsterCombatPhase.None),
            $"{mode}: the distant admission point does not activate the group");
        runtime.Advance(Start, [target]);
        Check.True(ScoringMonsters(runtime).All(monster =>
                monster.CombatPhase == MonsterCombatPhase.Chasing),
            $"{mode}: all twelve monsters acquire the player at (19,53)");

        var attacked = new HashSet<uint>();
        var now = Start;
        for (var tick = 0; tick < 420; tick++)
        {
            now += MonsterMapRuntime.TickInterval;
            var update = runtime.Advance(now, [target]);
            foreach (var attack in update.Updates.Where(value =>
                         value.Kind == MonsterRuntimeUpdateKind.Attacked))
            {
                Check.Equal(target.CharacterId, attack.TargetCharacterId!.Value,
                    "long-distance pursuit retains the intended live target");
                attacked.Add(attack.Monster.ObjectId);
            }
            Check.True(ScoringMonsters(runtime).All(monster =>
                    monster.CombatPhase is MonsterCombatPhase.Chasing or
                        MonsterCombatPhase.Attacking),
                $"{mode}: the enlarged leash allows pursuit to reach the trigger");
        }
        Check.True(monsters.All(monster => attacked.Contains(monster.ObjectId)),
            $"{mode}: every group member reaches attack range and attacks");

        runtime.Advance(now += MonsterMapRuntime.TickInterval,
            [target with { X = 300f }]);
        Check.True(ScoringMonsters(runtime).All(monster =>
                monster.CombatPhase == MonsterCombatPhase.Returning),
            $"{mode}: pursuit still has a finite boundary");
        for (var tick = 0; tick < 420; tick++)
        {
            runtime.Advance(now += MonsterMapRuntime.TickInterval, []);
        }
        Check.True(ScoringMonsters(runtime).All(monster =>
                monster.IsAlive && monster.SpawnGeneration == 1 &&
                monster.CombatPhase == MonsterCombatPhase.None),
            $"{mode}: leaving the leash returns the same never-respawn wave");

        var ordinary = MonsterMapRuntimeFactory.Create(mode, 205,
            monsters.Select(monster => monster.Definition), Start);
        ordinary.Advance(Start, [target]);
        Check.True(ordinary.Snapshot().All(monster =>
                monster.CombatPhase == MonsterCombatPhase.None),
            $"{mode}: a runtime without the Atlantis override retains ordinary aggro range");
    }

    private static void CheckIdleGrouping(MonsterRuntimeMode mode)
    {
        var runtime = CreateLiveWave(mode);
        var homes = ScoringMonsters(runtime).ToDictionary(monster => monster.ObjectId);
        var now = Start;
        for (var tick = 0; tick < 900; tick++)
        {
            runtime.Advance(now += MonsterMapRuntime.TickInterval, []);
            Check.True(ScoringMonsters(runtime).All(monster =>
                    Distance(monster.X, monster.Z, homes[monster.ObjectId].X,
                        homes[monster.ObjectId].Z) <=
                    AtlantisMonsterCombatPolicy.Behavior.MaximumRoamRadius + 0.0001),
                $"{mode}: idle movement stays within each verified spawn clearance");
        }
    }

    private static void CheckRoundedAttackBoundary(MonsterRuntimeMode mode)
    {
        var wave = ScoringMonsters(CreateLiveWave(mode));
        var profiles = MonsterCombatProfileCatalog.Create(AtlantisLiveWaveChecks.Content());
        foreach (var monster in new[] { wave[0], wave[5] })
        {
            var reach = MonsterAttackRangePolicy.Resolve(
                profiles.Resolve(monster.Definition), monster.Definition);
            var target = new MonsterCombatTarget(101,
                MathF.BitIncrement(monster.X + reach), monster.Z, true);
            var distance = Distance(monster.X, monster.Z, target.X, target.Z);
            Check.True(distance > reach && distance < reach + MonsterAttackRangePolicy.DistanceTolerance,
                "the fixture reproduces a float-rounded arrival just beyond exact reach");
            var runtime = MonsterMapRuntimeFactory.Create(mode, 205,
                [monster.Definition], Start, monsterCombatProfiles: profiles);
            runtime.Advance(Start, [target]);
            var tick = runtime.Advance(Start + MonsterMapRuntime.TickInterval, [target]);
            Check.True(tick.Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Attacked),
                $"{mode}: rounded melee/ranged arrival keeps the attack clock running");

            var outside = MonsterMapRuntimeFactory.Create(mode, 205,
                [monster.Definition], Start, monsterCombatProfiles: profiles);
            outside.Advance(Start, [target with { X = monster.X + reach + 0.01f }]);
            Check.True(outside.Snapshot().Single().CombatPhase == MonsterCombatPhase.Chasing,
                $"{mode}: the rounding tolerance does not grant meaningful extra attack reach");
        }
    }

    private static IMonsterMapRuntime CreateLiveWave(MonsterRuntimeMode mode)
    {
        var descriptor = WorldInstanceDescriptor.Create(RealmId.Tempest,
            WorldInstanceId.New(), new(205), InstanceKind.Dungeon, 5, Start);
        var map = new MapInstance(descriptor, mode);
        Check.True(map.TryConfigureAtlantisWaves(AtlantisLiveWaveChecks.Content(),
                [(101, 90)], Start) && map.TryStartAtlantisEncounter(Start, out _) &&
                map.TrySpawnPendingAtlantisWave(Start, out _),
            "the live map publishes a fully configured Atlantis group");
        return map.InitializeMonsters([], Start);
    }

    private static MonsterRuntimeSnapshot[] ScoringMonsters(IMonsterMapRuntime runtime) =>
        runtime.Snapshot().Where(monster => monster.ObjectId is >= 42_000 and < 42_245)
            .ToArray();

    private static double Distance(float x1, float z1, float x2, float z2) =>
        Math.Sqrt(Math.Pow((double)x2 - x1, 2) + Math.Pow((double)z2 - z1, 2));

    private static void CheckConfigurationValidation()
    {
        Check.True(MonsterBehaviorPolicy.Default.AggroDetectionRadius == 14f &&
            MonsterBehaviorPolicy.Default.CombatLeashRadius == 32f &&
            MonsterBehaviorPolicy.Default.MaximumRoamRadius == 8f,
            "the existing map defaults remain unchanged");
        foreach (var invalid in new[] { -1f, 0f, float.NaN, float.PositiveInfinity, float.MaxValue })
        {
            Check.Throws<ArgumentOutOfRangeException>(() =>
                new MonsterBehaviorPolicy(invalid, 128f, 2f),
                "invalid detection radius cannot enter either runtime");
        }
        Check.Throws<ArgumentOutOfRangeException>(() =>
            new MonsterBehaviorPolicy(112f, 32f, 2f),
            "detection cannot acquire targets outside the available leash");
        Check.Throws<ArgumentOutOfRangeException>(() =>
            new MonsterBehaviorPolicy(14f, 32f, 0.1f),
            "idle radius must contain the guaranteed fallback movement step");
    }
}
