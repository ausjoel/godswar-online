using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryResolveWonderlandFinalExit(ClientSession session, WorldInstanceId expectedInstance,
        long expectedLife, out AuthoritativeInstanceTransitionCommand command)
    {
        command = default;
        lock (_gate)
        {
            if (!TryCaptureWonderlandNpcInteraction(session, out var currentInstance) || currentInstance != expectedInstance ||
                !_sessions.TryGetValue(session, out var actor) ||
                !_playerLifeRevisions.TryGetValue(session, out var life) || life != expectedLife ||
                !_wonderlandAdmissions.TryGetValue(currentInstance, out var admission) ||
                !admission.Sealed || !admission.Entrants.Contains(actor.CharacterId) ||
                !TryGetWonderlandEncounterSnapshot(currentInstance, out var run) ||
                run.State != WonderlandRunState.Completed || run.CompletedIslands != 8 ||
                !_wonderlandTitleSettled.ContainsKey((currentInstance, 8)) || HasPendingWonderlandTitles(currentInstance))
                return false;
            var exit = WonderlandTraversalPolicy.GetTeleporter(8);
            var dx = (double)actor.Character.PositionX - exit.X;
            var dz = (double)actor.Character.PositionZ - exit.Z;
            if (!double.IsFinite(dx) || !double.IsFinite(dz) || dx * dx + dz * dz >
                WonderlandTransportProtocol.InteractionRadius * WonderlandTransportProtocol.InteractionRadius) return false;
            var targetMap = actor.Character.Camp == GameDefaults.SpartaCamp
                ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
            var target = GetOrCreateDefaultWorldInstance(targetMap);
            command = new(actor.CharacterId, currentInstance, WonderlandMapId, actor.Ownership,
                target.InstanceId, targetMap, GameDefaults.StartingPositionX, GameDefaults.StartingPositionZ)
                { RequiredLivingLifeRevision = expectedLife };
            return true;
        }
    }
}
