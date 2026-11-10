using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryCaptureWonderlandNpcInteraction(ClientSession session, out WorldInstanceId instanceId)
    {
        instanceId = default;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) || context.Character.CurrentHp <= 0 ||
                !IsCurrentWonderlandMember(context, context.WorldInstanceId) ||
                !TryGetWonderlandEncounterSnapshot(context.WorldInstanceId, out var run) ||
                !run.Participants.Any(member => member.CharacterId == context.CharacterId) ||
                !WonderlandCompletionPolicy.AllowsIslandTravel(run, DateTimeOffset.UtcNow)) return false;
            instanceId = context.WorldInstanceId;
            return true;
        }
    }

    internal bool TryResolveWonderlandTravel(ClientSession session, int sourceIsland,
        out WorldInstanceId instanceId, out WonderlandPosition target)
    {
        instanceId = default;
        target = default;
        if (sourceIsland is < 1 or > 7) return false;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) || context.Character.CurrentHp <= 0 ||
                !IsCurrentWonderlandMember(context, context.WorldInstanceId) ||
                !TryGetWonderlandEncounterSnapshot(context.WorldInstanceId, out var run) ||
                !WonderlandCompletionPolicy.AllowsIslandTravel(run, DateTimeOffset.UtcNow) ||
                run.CompletedIslands < sourceIsland) return false;
            instanceId = context.WorldInstanceId;
            target = WonderlandTerrainPolicy.GetIsland(sourceIsland + 1).Entrance;
            return true;
        }
    }

    internal bool IsWonderlandTravelCurrent(ClientSession session, WorldInstanceId expectedInstance, int sourceIsland)
    {
        lock (_gate)
            return _sessions.TryGetValue(session, out var context) &&
                IsCurrentWonderlandMember(context, expectedInstance) &&
                TryGetWonderlandEncounterSnapshot(expectedInstance, out var run) &&
                WonderlandCompletionPolicy.AllowsIslandTravel(run, DateTimeOffset.UtcNow) &&
                run.CompletedIslands >= sourceIsland;
    }

    internal bool TryReviveWonderlandPlayer(ClientSession session, out WorldInstanceId instanceId,
        out WonderlandPosition entrance, out long lifeRevision)
    {
        instanceId = default;
        entrance = default;
        lifeRevision = -1;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                !IsCurrentWonderlandMember(context, context.WorldInstanceId) ||
                !TryGetWonderlandEncounterSnapshot(context.WorldInstanceId, out var run) ||
                !WonderlandCompletionPolicy.AllowsIslandTravel(run, DateTimeOffset.UtcNow)) return false;
            lock (context.Character.VitalsSync)
            {
                if (context.Character.CurrentHp > 0) return false;
                lifeRevision = AdvancePlayerLifeRevision(session);
                if (lifeRevision < 0) return false;
                context.Character.CurrentHp = Math.Max(1, context.Character.MaxHp / 10);
                context.Character.CurrentMp = Math.Max(0, context.Character.MaxMp / 10);
                context.Character.MarkVitalsChanged();
            }
            instanceId = context.WorldInstanceId;
            // Free revival always returns to the entrance island. The run's
            // unlocked progress is retained for the Blackmarket return service.
            entrance = WonderlandTerrainPolicy.GetIsland(1).Entrance;
            return true;
        }
    }
}
