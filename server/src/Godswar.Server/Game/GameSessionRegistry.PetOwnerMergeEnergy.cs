using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool IsPetOwnerMergeEnergyProtected(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Keep the existing bound Medusa exemption; content-map IDs alone
        // never authorize any of the instance exemptions.
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var current) || session.IsDisconnected ||
                current.Character.CurrentMap != current.MapId ||
                !IsCurrentAccountSession(current.AccountId, session, current.Ownership) ||
                !TryGetWorldInstance(current, out var runtime))
                return false;
            if (current.MapId is 200 or 204) return IsSessionInMedusaInstance(session);
            if (runtime.Descriptor.Kind != InstanceKind.Dungeon) return false;
            if (current.MapId == AtlantisEncounterPolicy.ContentMapId.Value)
                return InvokeWorldOwner(runtime, map =>
                    map.HasAdmittedAtlantisPetOwner(current.AccountId, current.CharacterId));
            if (current.MapId != WonderlandMapId ||
                !_wonderlandAdmissions.TryGetValue(current.WorldInstanceId, out var admission) ||
                !admission.Entrants.Contains(current.CharacterId) ||
                !admission.OriginalMembers.Any(member => member.Session == session &&
                    member.AccountId == current.AccountId && member.CharacterId == current.CharacterId &&
                    member.Ownership == current.Ownership)) return false;
            // Completed, canceled and expired runs remain protected while an
            // admitted player waits for egress. The next ordinary-map tick drains
            // exactly one point; protected intervals are never accumulated.
            return InvokeWorldOwner(runtime, map => map.TryGetWonderlandSnapshot(out _));
        }
    }
}
