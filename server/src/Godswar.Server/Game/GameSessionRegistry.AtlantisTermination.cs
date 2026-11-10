using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // Atlantis keeps its leader in the one per-run leader identity every dungeon
    // shares; this name only keeps the run's own paper trail readable.
    private ConcurrentDictionary<WorldInstanceId, int> _atlantisLeaderCharacterIds =>
        _instanceRunLeaders;
    private readonly ConcurrentDictionary<WorldInstanceId, byte> _atlantisTerminationExitRequested = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte> _atlantisTerminationEgressInFlight = [];

    private void ForgetAtlantisTermination(WorldInstanceId instanceId)
    {
        ForgetInstanceRunEnd(instanceId);
        _atlantisTerminationExitRequested.TryRemove(instanceId, out _);
        _atlantisTerminationEgressInFlight.TryRemove(instanceId, out _);
    }

    internal bool TryTerminateAtlantisRunFromLeader(ClientSession session, DateTimeOffset requestedAt) =>
        TryEndAtlantisRunFromLeader(session, AtlantisClientSceneId, 0, requestedAt);

    internal bool TryEndAtlantisRunFromLeader(ClientSession session, int repetitionId,
        int repetitionIndex, DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        GameSessionContext actor;
        WorldInstanceRuntime runtime;
        lock (_gate)
        {
            if (repetitionId != AtlantisClientSceneId || repetitionIndex != 0 ||
                !_sessions.TryGetValue(session, out actor!) ||
                !IsCurrentAtlantisTerminationLeader(actor) ||
                // Party leadership is a separate system and must not gate the
                // instance's own end control; only its leader may end it.
                !WorldInstances.TryFind(actor.WorldInstanceId, out runtime!) ||
                !AtlantisEncounterPolicy.IsAtlantisInstance(runtime.Descriptor))
            {
                return false;
            }
            // Match membership transition ordering: registry serialization
            // precedes this owner command, which never acquires the registry
            // gate. Leadership and ownership cannot change during cancellation.
            return InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
            {
                if (!IsCurrentAtlantisTerminationLeader(actor) ||
                    map.WorldInstanceId != actor.WorldInstanceId ||
                    !map.Snapshot().Any(member => ReferenceEquals(member.Session, session) &&
                        member.CharacterId == actor.CharacterId && member.Ownership == actor.Ownership) ||
                    !map.TryCancelAtlantisEncounter(requestedAt, out _))
                {
                    return false;
                }
                _atlantisTerminationExitRequested.TryAdd(actor.WorldInstanceId, 0);
                return true;
            });
        }
    }

    /// <summary>
    /// Moves Atlantis's leader to the earliest still-present member when the
    /// registered leader has left the run, through the one leader-transfer
    /// implementation every dungeon shares.
    /// </summary>
    private void MaintainAtlantisLeader(AtlantisRunDelivery delivery)
    {
        // The delivery's member order is the run's own capture order; the
        // character id breaks ties for members it does not order.
        MaintainInstanceRunLeader(
            delivery.Runtime.InstanceId,
            "Atlantis",
            [.. delivery.Members.Select(static member => member.CharacterId)],
            [.. delivery.Members.Select(static member => member.CharacterId)]);
    }

    private bool IsCurrentAtlantisTerminationLeader(GameSessionContext actor) =>
        !actor.Session.IsDisconnected && actor.WorldReady &&
        actor.MapId == DynamicDungeonContentMapPolicy.AtlantisPortalMapId &&
        actor.Character.CurrentMap == actor.MapId && actor.Ownership.IsValid &&
        TryGetInstanceRunLeader(actor.WorldInstanceId, out var leaderId) &&
        leaderId == actor.CharacterId &&
        IsCurrentAccountSession(actor.AccountId, actor.Session, actor.Ownership);
}
