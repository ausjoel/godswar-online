using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // One deadline per controlled actor generation. Refreshes replace the old
    // deadline instead of accumulating timer tasks or stale heap entries.
    private readonly Dictionary<MonsterControlIdentity, MonsterControlExpiry> _monsterControlExpiries = [];
    private readonly SortedSet<MonsterControlExpiry> _monsterControlDeadlines = new(
        Comparer<MonsterControlExpiry>.Create((left, right) =>
        {
            var time = left.At.CompareTo(right.At);
            return time != 0 ? time : left.Sequence.CompareTo(right.Sequence);
        }));
    private long _monsterControlExpirySequence;
    private DateTimeOffset _monsterControlObservedAt;

    private void TrackMonsterControlExpiry(WorldInstanceRuntime runtime,
        MonsterRuntimeSnapshot expected, DateTimeOffset now)
    {
        lock (_gate)
        {
            var current = InvokeWorldOwner(runtime, map =>
                map.TryGetMonsterSnapshot(expected.ObjectId, out var actor) ? actor : null);
            if (current is null || current.RuntimeInstanceId != expected.RuntimeInstanceId ||
                current.SpawnGeneration != expected.SpawnGeneration) return;
            ScheduleMonsterControlExpiryLocked(runtime, current,
                ResolveMonsterControlTimeLocked(runtime, now));
        }
    }

    private DateTimeOffset ResolveMonsterControlTimeLocked(WorldInstanceRuntime runtime, DateTimeOffset now)
    {
        if (now < _monsterControlObservedAt) now = _monsterControlObservedAt;
        // Status expiry continues during completion countdowns, even when the
        // combat admission window has closed. Only the owner's clock is shared.
        if (runtime.MapId == 207)
        {
            if (runtime.Map.TryGetWonderlandSnapshot(out var run) && now < run.LastObservedAt)
                now = run.LastObservedAt;
            if (_wonderlandCombat.TryGetValue(runtime.InstanceId, out var combat) && now < combat.LastAdvancedAt)
                now = combat.LastAdvancedAt;
        }
        return now;
    }

    private void ScheduleMonsterControlExpiryLocked(WorldInstanceRuntime runtime,
        MonsterRuntimeSnapshot current, DateTimeOffset now)
    {
        if (now > _monsterControlObservedAt) _monsterControlObservedAt = now;
        var identity = new MonsterControlIdentity(runtime.InstanceId, current.RuntimeInstanceId,
            current.ObjectId, current.SpawnGeneration);
        DateTimeOffset? next = current.IsAlive && current.IsSpawned
            ? current.Controls.Active(now).Select(entry => (DateTimeOffset?)entry.ExpiresAt).Min()
            : null;
        if (current.IsAlive && current.IsSpawned && current.StunnedUntil is { } stunned && stunned > now &&
            (next is null || stunned < next)) next = stunned;
        if (_monsterControlExpiries.TryGetValue(identity, out var previous))
        {
            // A new viewer can hydrate after expiry or after death has cleared
            // the server state. Keep the clear pending for existing viewers.
            if (previous.At <= now || next is null) return;
            if (previous.At == next) return;
            _monsterControlExpiries.Remove(identity);
            _monsterControlDeadlines.Remove(previous);
        }
        if (next is null) return;
        var deadline = new MonsterControlExpiry(identity, next.Value, checked(++_monsterControlExpirySequence));
        _monsterControlExpiries.Add(identity, deadline);
        _monsterControlDeadlines.Add(deadline);
    }

    internal async Task ReconcileMonsterControlsOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deliveries = new List<(WorldInstanceRuntime Runtime, MonsterRuntimeSnapshot Monster)>();
        lock (_gate)
        {
            if (now > _monsterControlObservedAt) _monsterControlObservedAt = now;
            while (_monsterControlDeadlines.Count > 0 && _monsterControlDeadlines.Min!.At <= now)
            {
                var due = _monsterControlDeadlines.Min!;
                _monsterControlDeadlines.Remove(due);
                _monsterControlExpiries.Remove(due.Identity);
                if (!WorldInstances.TryFind(due.Identity.WorldInstanceId, out var runtime) ||
                    runtime.Descriptor.LifecycleState == WorldInstanceLifecycleState.Closed) continue;
                var current = InvokeWorldOwner(runtime, map =>
                    map.TryGetMonsterSnapshot(due.Identity.ObjectId, out var actor) ? actor : null);
                if (current is null || !current.IsSpawned ||
                    current.RuntimeInstanceId != due.Identity.RuntimeInstanceId ||
                    current.SpawnGeneration != due.Identity.SpawnGeneration) continue;
                ScheduleMonsterControlExpiryLocked(runtime, current, ResolveMonsterControlTimeLocked(runtime, now));
                deliveries.Add((runtime, current));
            }
        }
        // Network backpressure and viewer transitions must never hold _gate.
        // The composer reads the latest state again after each viewer lease.
        foreach (var delivery in deliveries)
            await PublishMonsterControlsAsync(delivery.Runtime, delivery.Monster, cancellationToken, now);
    }

    private readonly record struct MonsterControlIdentity(WorldInstanceId WorldInstanceId,
        Guid RuntimeInstanceId, uint ObjectId, uint SpawnGeneration);
    private sealed record MonsterControlExpiry(MonsterControlIdentity Identity, DateTimeOffset At, long Sequence);
}
