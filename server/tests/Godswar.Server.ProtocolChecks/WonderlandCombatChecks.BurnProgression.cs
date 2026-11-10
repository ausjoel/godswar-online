using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string BurnProgressionCheckName =
        "Wonderland periodic burn kills clear island six exactly once and unlock its transporter";

    public static async Task RunBurnProgressionAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            await using var f = await Fixture.CreateAsync(6, mode, PlayerRuntimeMode.Ecs);
            Check.True(!f.Registry.TryResolveWonderlandTravel(f.Session, 6, out _, out _),
                "the living Platinum Dragon still gates island seven");
            await KillWonderlandDragonByBurnAsync(f.Registry, f.Runtime, f.Session, f.Character, f.Now);
            Check.True(f.Registry.TryResolveWonderlandTravel(f.Session, 6, out var instance, out var arrival) &&
                instance == f.Runtime.InstanceId &&
                arrival == WonderlandTerrainPolicy.GetIsland(7).Entrance &&
                f.Registry.HasPendingWonderlandTitles(f.Runtime.InstanceId),
                $"{mode}: a periodic lethal hit unlocks normal travel and queues the exact island milestone");
        }
    }

    internal static async Task<DateTimeOffset> KillWonderlandDragonByBurnAsync(GameSessionRegistry registry,
        WorldInstanceRuntime runtime, ClientSession session, GameCharacter character, DateTimeOffset now)
    {
        var profile = new ElementalEquipmentProfile(
            new Dictionary<ElementKind, ElementalEffectTotals>
            { [ElementKind.Fire] = new(1_000, 0, 10_000) },
            new Dictionary<ElementKind, int> { [ElementKind.Fire] = 1 },
            new Dictionary<ElementKind, IReadOnlyList<ElementalResonanceTierDefinition>>());
        typeof(GameCharacter).GetProperty(nameof(GameCharacter.ElementalEquipment))!.SetValue(character, profile);
        var dragon = runtime.Map.SnapshotMonsters().Single(monster => monster.ObjectId == 46600);
        character.PositionX = dragon.X;
        character.PositionZ = dragon.Z;
        registry.UpdateCharacter(session, character, advanceWorldRevision: false);
        using var authority = registry.CapturePveElementalCommitAuthority(session, character)
            ?? throw new InvalidOperationException("Missing exact Fire-hit authority.");
        Check.True(registry.TryCapturePlayerMonsterTarget(session, 207, dragon.ObjectId,
            out var target, out var targetAuthority), "Burn fixture captures the real dragon identity");
        var eventId = FindWonderlandFireEvent(character, dragon.ObjectId, now);
        var resolution = AuthoredCombatPveCurrent.ResolveBasicAttack(new CombatAttackerStats
            { Level = character.Level, PhysicalAttack = 10_000, Hit = 10_000 },
            new CombatTargetStats { Level = 130 }, eventId) with
            { Damage = dragon.CurrentHealth - 1, Outcome = CombatHitOutcome.Normal };
        Check.True(registry.TryCommitPlayerMonsterDamageGuarded(session, 207, target.ObjectId,
                target.RuntimeInstanceId, character.Id, target.SpawnGeneration, target.HealthRevision,
                targetAuthority, now, resolution, out var direct) &&
            direct.DamageResult is { Killed: false, AfterHealth: 1 },
            "the direct hit leaves the dragon alive; only the subsequent Burn may clear it");
        var applied = registry.CommitPveElementalHits(authority, CombatEventProvenance.DirectBasicAttack,
            [new(eventId, 0, direct.DamageResult!)], now);
        Check.True(applied.Applications is [{ Effect: ElementalEffectKind.Burn }] &&
            applied.DamageCommits.Count == 0,
            "the real elemental transaction installs Burn without a lethal resonance shortcut");
        await registry.AdvancePlayerRecoveryOnceAsync(now.AddMilliseconds(999), CancellationToken.None);
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var before) && before.CompletedIslands == 5 &&
            runtime.Map.TryGetMonsterSnapshot(dragon.ObjectId, out var living) && living.CurrentHealth == 1,
            "the island gate remains closed before the authored first Burn tick");
        var tick = now.AddSeconds(1);
        await registry.AdvancePlayerRecoveryOnceAsync(tick, CancellationToken.None);
        Check.True(runtime.Map.TryGetMonsterSnapshot(dragon.ObjectId, out var dead) && !dead.IsAlive &&
            dead.CurrentHealth == 0, "the real periodic clock kills Platinum Dragon");
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var cleared) && cleared.CompletedIslands == 6 &&
            cleared.CurrentIsland == 7 && cleared.PublicationPending,
            "periodic death advances Wonderland's island ledger before any next-stage publication");
        await registry.AdvancePlayerRecoveryOnceAsync(tick, CancellationToken.None);
        Check.True(runtime.Map.TryGetMonsterSnapshot(dragon.ObjectId, out var replay) &&
            replay.HealthRevision == dead.HealthRevision &&
            runtime.Map.TryGetWonderlandSnapshot(out var replayRun) && replayRun.CompletedIslands == 6,
            "replaying the same periodic deadline cannot duplicate death or island progression");
        return tick;
    }

    private static ulong FindWonderlandFireEvent(GameCharacter character, uint objectId, DateTimeOffset now)
    {
        for (ulong id = 880_000; id < 890_000; id++)
        {
            var context = new DeterministicCombatEventContext(id, 207, character.Id, objectId,
                now.ToUnixTimeMilliseconds(), CombatEventProvenance.DirectBasicAttack, true, false, default);
            if (ElementalEffectExecutionPolicy.DeterministicRollBasisPoints(context, ElementKind.Fire) < 2_000)
                return id;
        }
        throw new InvalidOperationException("No deterministic Fire event found.");
    }
}
