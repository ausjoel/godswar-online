using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    // Native 10046 discards an effect whose source actor was never hydrated.
    // Keep the real dragon available across its own island while it powers the
    // terrain hazard. Ordinary corpse/loot visibility is deliberately unchanged.
    private bool IsWonderlandGroundFireSourceVisible(MonsterRuntimeSnapshot monster, float x, float z) =>
        MapId == 207 && monster.IsAlive && monster.IsSpawned &&
        WonderlandTerrainPolicy.GetIsland(6).Bounds.Contains(x, z) &&
        TryGetWonderlandSpawnPolicy(monster.ObjectId, out var policy) &&
        policy.Stage == 6 && policy.MechanicKey == "platinum" &&
        TryGetWonderlandSnapshot(out var run) && run.State == WonderlandRunState.Active &&
        (run.ActiveStage > 6 || run.ActiveStage == 6 && !run.PublicationPending);

    internal async ValueTask<MonsterViewerDeliveryLease?> AcquireWonderlandFireSourceLeaseAsync(
        ClientSession session, MonsterRuntimeSnapshot expected, CancellationToken cancellationToken)
    {
        if (!ContainsPlayer(session)) return null;
        var viewer = _monsterViewers.GetOrAdd(session, static _ => new MonsterViewerState());
        // Wait outside registry, actor and world-owner locks, just as ordinary
        // monster delivery does. The caller rechecks exact authority afterwards.
        await viewer.TransitionGate.WaitAsync(cancellationToken);
        try
        {
            if (!ContainsPlayer(session) || !_monsterViewers.TryGetValue(session, out var currentViewer) ||
                !ReferenceEquals(currentViewer, viewer) ||
                !TryGetMonsterSnapshot(expected.ObjectId, out var source) ||
                source.RuntimeInstanceId != expected.RuntimeInstanceId ||
                source.SpawnGeneration != expected.SpawnGeneration || !source.IsAlive || !source.IsSpawned)
            {
                viewer.TransitionGate.Release();
                return null;
            }
            if (viewer.VisibleMonsterVersions.TryGetValue(source.ObjectId, out var visible) &&
                visible.SpawnGeneration == source.SpawnGeneration)
                return new MonsterViewerDeliveryLease(viewer, [], [], [], []);
            return new MonsterViewerDeliveryLease(viewer, [], [source.ObjectId], [source], []);
        }
        catch
        {
            viewer.TransitionGate.Release();
            throw;
        }
    }
}
