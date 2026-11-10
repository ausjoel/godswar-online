using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandMapChecks
{
    private static void CheckCompletedCombatWindow(MonsterRuntimeMode mode)
    {
        var map = Create(mode);
        EnterIsland(map, 8);
        map.TryGetWonderlandSnapshot(out var before);
        var now = before.Deadline.AddSeconds(-1);
        foreach (var required in before.ActiveSpawns.Where(actor => actor.RequiredForProgression))
            Kill(map, required.ObjectId, now = now.AddMilliseconds(1));
        map.TryGetWonderlandSnapshot(out var complete);
        var ends = complete.TerminalAt!.Value + WonderlandCompletionPolicy.TreasureWindow;
        Check.True(map.AdvanceWonderland(Start).State == WonderlandRunState.Completed &&
            !map.TrySpawnPendingWonderlandStage(Start, out _) &&
            map.RecordCommittedWonderlandKill(map.WorldInstanceId,
                before.ActiveSpawns.First(actor => actor.RequiredForProgression).ObjectId, 1, Start).Outcome ==
                WonderlandKillOutcome.StaleTimestamp,
            "stale ticks and duplicate kills preserve the current completed snapshot");
        var afterOriginalDeadline = before.Deadline.AddSeconds(1);
        Check.True(map.AdvanceWonderland(afterOriginalDeadline).State == WonderlandRunState.Completed &&
            !map.TrySpawnPendingWonderlandStage(afterOriginalDeadline, out _),
            "completion keeps the five-minute combat window without publishing another island");
        var survivorPolicy = complete.ActiveSpawns.First(actor => actor.Role == WonderlandMonsterRole.Atlas);
        map.TryGetMonsterSnapshot(survivorPolicy.ObjectId, out var survivor);
        Check.True(!map.TryApplyMonsterDamageGuarded(survivor.ObjectId, 1, 999, survivor.SpawnGeneration,
                survivor.HealthRevision, afterOriginalDeadline, out _) &&
            !map.TryApplyMonsterDamageGuarded(survivor.ObjectId, 1, 101, survivor.SpawnGeneration + 1,
                survivor.HealthRevision, afterOriginalDeadline, out _) &&
            map.TryApplyMonsterDamageGuarded(survivor.ObjectId, 1, 101, survivor.SpawnGeneration,
                survivor.HealthRevision, afterOriginalDeadline, out var direct) && direct.AfterHealth == survivor.CurrentHealth - 1,
            "completed combat still requires the original entrant and exact monster generation");
        Check.True(map.TryApplyMonsterPeriodicDamageGuarded(survivor.ObjectId, 1, 101, survivor.SpawnGeneration,
                survivor.HealthRevision + 1, afterOriginalDeadline, out var periodic) && periodic.AfterHealth == survivor.CurrentHealth - 2,
            "periodic damage remains authoritative beyond the original run deadline");
        var runtime = map.InitializeMonsters([], Start);
        var updates = new List<MonsterRuntimeUpdate>();
        for (var tick = 0; tick < 12; tick++)
            updates.AddRange(runtime.Advance(afterOriginalDeadline.AddMilliseconds(250 * tick),
                [new(101, survivor.X, survivor.Z, true, WorldInstanceId: map.WorldInstanceId)]).Updates);
        Check.True(updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Attacked &&
            update.Monster.ObjectId == survivor.ObjectId),
            $"{mode}: the living optional Atlas still attacks during the completion treasure window");
        var earlier = map.SnapshotMonsters().First(actor => actor.IsAlive && actor.IsSpawned &&
            map.TryGetWonderlandSpawnPolicy(actor.ObjectId, out var policy) && policy.Stage == 3 && !policy.IsBoss);
        Check.True(Kill(map, earlier.ObjectId, ends.AddSeconds(-1)).Outcome == WonderlandKillOutcome.RunNotActive,
            "killing an optional survivor cannot create another progression reward");
        map.TryGetWonderlandSnapshot(out var afterOptionalKill);
        Check.True(afterOptionalKill.TerminalAt == complete.TerminalAt && afterOptionalKill.Clears.SequenceEqual(complete.Clears) &&
            afterOptionalKill.CompletedIslands == 8,
            "optional combat preserves all earned milestones and the original completion deadline");
        map.TryGetMonsterSnapshot(survivor.ObjectId, out var current);
        Check.True(map.TryApplyMonsterPeriodicDamageGuarded(survivor.ObjectId, 1, 101, current.SpawnGeneration,
            current.HealthRevision, ends.AddTicks(-1), out _), "the final tick before expiry remains playable");
        map.TryGetMonsterSnapshot(survivor.ObjectId, out current);
        Check.True(!map.TryApplyMonsterDamageGuarded(survivor.ObjectId, 1, 101, current.SpawnGeneration,
                current.HealthRevision, ends, out _) &&
            !runtime.TryApplyStun(survivor.ObjectId, 101, TimeSpan.FromSeconds(2), current.SpawnGeneration, ends, out _),
            "damage and control stop at the exact five-minute boundary even before another world tick");
        var foreignUpdates = runtime.Advance(ends, [new(101, survivor.X, survivor.Z, true, WorldInstanceId: WorldInstanceId.New())]);
        Check.True(foreignUpdates.Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked) &&
            map.SnapshotMonsters().Where(actor => actor.IsAlive).All(actor => !actor.IsMoving),
            "expired survivors stop without gaining a new target or being silently killed");
    }
}
