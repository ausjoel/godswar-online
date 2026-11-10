using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal IWonderlandBlackmarketStore? WonderlandBlackmarket { get; private set; }
    internal void ConfigureWonderlandBlackmarket(IWonderlandBlackmarketStore? store) => WonderlandBlackmarket = store;

    internal bool TryResolveWonderlandBlackmarket(ClientSession session, WorldInstanceId instance,
        out int targetIsland, out WonderlandPosition target, out Guid reservationId)
    {
        targetIsland = 0;
        target = default;
        reservationId = default;
        lock (_gate)
        {
            if (!TryCaptureWonderlandNpcInteraction(session, out var current) || current != instance ||
                !TryGetWonderlandEncounterSnapshot(instance, out var run) ||
                !_wonderlandAdmissions.TryGetValue(instance, out var admission) || !admission.Sealed) return false;
            reservationId = admission.ReservationId;
            targetIsland = Math.Min(8, run.CompletedIslands + 1);
            target = WonderlandTerrainPolicy.GetIsland(targetIsland).Entrance;
            return true;
        }
    }
}
