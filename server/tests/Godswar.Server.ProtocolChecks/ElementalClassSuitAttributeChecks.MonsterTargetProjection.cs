using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    internal const string MonsterTargetProjectionCheckName =
        "Prepared monster targets preserve exact membership and life authority outside the owner";

    internal static async Task RunMonsterTargetProjectionAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var change in new[]
        {
            "character", "object", "ownership", "instance", "revision", "epoch", "life", "unready", "removed"
        })
            await CheckPreparedMonsterTargetAsync(mode, change);
    }

    private static async Task CheckPreparedMonsterTargetAsync(MonsterRuntimeMode mode, string change)
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        var map = new MapInstance(0, mode);
        await using var runtime = new WorldInstanceRuntime(map);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var character = ElementalLiveCharacter(1_425, 65, ownership);
        var context = new GameSessionContext(socket.Session, character.AccountId, character.Id, character.Name,
            map.RealmId, map.WorldInstanceId, map.MapId, WorldObjectIds.ForPlayer(character.Id), character,
            WorldReady: true, WorldRevision: 7) { Ownership = ownership, WorldMembershipEpoch = 4 };
        var at = DateTimeOffset.UtcNow;
        const uint objectId = 9_425;
        map.InitializeMonsters([ElementalReachMonster(objectId, "ElementalPreparedTarget")], at);
        map.AddOrUpdate(context);
        long? currentLife = 5;
        var captured = MapInstance.CaptureMonsterCombatTargets(
            runtime.Owner.Invoke(static ownerMap => ownerMap.Snapshot(), TimeSpan.FromSeconds(3)),
            _ => currentLife);
        var target = captured.Single();
        Check.True(target.CharacterId == character.Id && target.ObjectId == context.ObjectId &&
            target.WorldInstanceId == context.WorldInstanceId && target.WorldRevision == 7 &&
            target.WorldMembershipEpoch == 4 && target.Ownership == ownership && target.LifeRevision == 5 &&
            target.IsAlive && target.X == character.PositionX && target.Z == character.PositionZ,
            "prepared targets contain only the captured scalar combat state and exact membership/life identity");
        Check.Throws<InvalidOperationException>(() => runtime.Owner.Invoke(
            ownerMap => ownerMap.AdvanceMonsters(at, _ => currentLife), TimeSpan.FromSeconds(3)),
            "the convenience path rejects vitals projection from inside an owner callback");
        Check.True(runtime.Owner.Invoke(ownerMap => ownerMap.TryApplyMonsterDamage(
            objectId, 1, character.Id, at, out _), TimeSpan.FromSeconds(3)),
            "the target-fence fixture acquires real monster aggro");
        _ = runtime.Owner.Invoke(ownerMap => ownerMap.AdvanceMonsters(at, captured, _ => currentLife),
            TimeSpan.FromSeconds(3));
        var initial = runtime.Owner.Invoke(ownerMap => ownerMap.AdvanceMonsters(
            at.AddSeconds(1), captured, _ => currentLife), TimeSpan.FromSeconds(3));
        Check.True(initial.Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Attacked &&
            update.TargetCharacterId == target.CharacterId && update.TargetObjectId == target.ObjectId &&
            update.TargetWorldRevision == target.WorldRevision && update.TargetLifeRevision == 5 &&
            update.TargetWorldMembershipEpoch == target.WorldMembershipEpoch && update.TargetOwnership == ownership),
            $"{mode}: an exact current projection emits a fully fenced real attack before {change} changes");

        // These are stale values received after projection, not newly inferred
        // authority. The owner must never repair them into a current identity.
        target = change switch
        {
            "character" => target with { CharacterId = target.CharacterId + 1 },
            "object" => target with { ObjectId = target.ObjectId + 1 },
            "ownership" => target with { Ownership = new(Guid.NewGuid(), 2) },
            "instance" => target with { WorldInstanceId = WorldInstanceId.New() },
            "revision" => target with { WorldRevision = target.WorldRevision - 1 },
            "epoch" => target with { WorldMembershipEpoch = target.WorldMembershipEpoch - 1 },
            _ => target
        };
        if (change == "life") currentLife++;
        if (change == "unready") map.AddOrUpdate(context with { WorldReady = false });
        if (change == "removed") Check.True(map.Remove(socket.Session, out _), "the captured member departs");
        var stale = runtime.Owner.Invoke(ownerMap => ownerMap.AdvanceMonsters(
            at.AddSeconds(10), new[] { target }, _ => currentLife), TimeSpan.FromSeconds(3));
        Check.True(!stale.Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Attacked),
            $"{mode}: a stale {change} projection is excluded before the monster can emit a new attack");
        Check.True(runtime.Owner.Invoke(static ownerMap => ownerMap.SnapshotMonsters().Count,
            TimeSpan.FromSeconds(3)) == 1, "rejection does not fault or drain the world owner");
    }
}
