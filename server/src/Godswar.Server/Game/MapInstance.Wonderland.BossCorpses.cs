using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private readonly Dictionary<uint, WonderlandBossCorpse> _wonderlandBossCorpses = [];
    private DateTimeOffset? _wonderlandBossCorpseRetirementAt;

    private void RetainCommittedWonderlandBossCorpse(WonderlandKillResult result,
        MonsterRuntimeSnapshot monster, DateTimeOffset diedAt)
    {
        if (result.Outcome is not (WonderlandKillOutcome.Applied or WonderlandKillOutcome.IslandCleared or
                WonderlandKillOutcome.RunCompleted) ||
            !WonderlandBossLootPolicy.TryResolve(monster.ObjectId, result.Snapshot.PartyCamp, out var definition) ||
            _wonderlandBossCorpses.ContainsKey(monster.ObjectId)) return;
        if (!_wonderlandMonsters!.TrySetCorpseDespawnAt(monster.ObjectId, monster.SpawnGeneration,
                WonderlandBossLootPolicy.CorpseExpiresAt(result.Snapshot, diedAt)))
            throw new InvalidOperationException("Committed Wonderland boss corpse could not be retained.");
        _wonderlandBossCorpses.Add(monster.ObjectId, new(definition, monster.SpawnGeneration,
            Guid.NewGuid(), diedAt, monster.X, monster.Z));
    }

    private void ScheduleWonderlandBossCorpseRetirement(WonderlandSnapshot run)
    {
        if (run.State == WonderlandRunState.Active || run.TerminalAt is not { } terminal) return;
        var expiresAt = run.State == WonderlandRunState.Completed
            ? terminal + WonderlandCompletionPolicy.TreasureWindow : terminal;
        if (_wonderlandBossCorpseRetirementAt == expiresAt) return;
        lock (_monsterRuntimeGate)
            foreach (var corpse in _wonderlandBossCorpses.Values)
                _wonderlandMonsters!.TrySetCorpseDespawnAt(corpse.Definition.MonsterObjectId,
                    corpse.SpawnGeneration, WonderlandBossLootPolicy.CorpseExpiresAt(run, corpse.DiedAt));
        _wonderlandBossCorpseRetirementAt = expiresAt;
    }

    internal IReadOnlyList<WonderlandBossCorpse> SnapshotWonderlandBossCorpses()
    {
        lock (_wonderlandGate) return _wonderlandBossCorpses.Values.ToArray();
    }

    internal bool TryGetWonderlandBossCorpse(uint objectId, out WonderlandBossCorpse corpse)
    {
        lock (_wonderlandGate) return _wonderlandBossCorpses.TryGetValue(objectId, out corpse!);
    }

    internal bool IsRetainedWonderlandBossCorpse(MonsterRuntimeSnapshot monster) =>
        !monster.IsAlive && monster.IsSpawned && TryGetWonderlandBossCorpse(monster.ObjectId, out var corpse) &&
        corpse.SpawnGeneration == monster.SpawnGeneration;
}

internal sealed record WonderlandBossCorpse(WonderlandBossLootDefinition Definition, uint SpawnGeneration,
    Guid DeathEventId, DateTimeOffset DiedAt, float X, float Z);
