using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// The exact instance a logging-in character is put back into.
    /// </summary>
    internal readonly record struct InstanceReconnectTarget(
        WorldInstanceId InstanceId,
        byte MapId,
        float EntranceX,
        float EntranceZ,
        InstanceRunKind Kind);

    /// <summary>
    /// Resolves the running instance a saved dynamic-dungeon map still belongs
    /// to, so a character who drops inside a run can be put back into it.
    /// </summary>
    /// <remarks>
    /// Every condition the operator asked for is checked here, and nothing is
    /// changed: the run's runtime must still exist and be active, the run itself
    /// must still be running, the character must still be on that run's own
    /// admission record, and at least one other player must still be inside the
    /// instance. A run with nobody else in it is deliberately not returned - the
    /// empty runtime is retired by the tick, and the character goes home instead.
    /// <para>
    /// Runs without the registry gate on purpose: it only reads concurrent
    /// collections and invokes map owners, and the caller joins the instance
    /// afterwards, where every authority check is repeated under the gate.
    /// </para>
    /// </remarks>
    internal bool TryResolveInstanceReconnect(
        int characterId,
        int accountId,
        RealmId realmId,
        byte savedMapId,
        out InstanceReconnectTarget target)
    {
        target = default;
        if (characterId <= 0 || accountId <= 0 ||
            !DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(savedMapId) ||
            !TryResolveInstanceEntrance(savedMapId, out var entranceX,
                out var entranceZ))
        {
            return false;
        }

        foreach (var (instanceId, kind) in CandidateInstanceReconnects())
        {
            if (!WorldInstances.TryFind(instanceId, out var runtime) ||
                runtime.MapId != savedMapId ||
                runtime.RealmId != realmId ||
                runtime.Descriptor.LifecycleState !=
                    WorldInstanceLifecycleState.Active ||
                !IsInstanceReconnectRunActive(runtime, kind) ||
                !IsInstanceReconnectMember(runtime, kind, accountId,
                    characterId))
            {
                continue;
            }

            // "Nobody else is in there" is not a run to re-enter: the empty
            // runtime is retired and the character belongs in the capital.
            if (!LiveInstanceMembers(instanceId)
                    .Any(member => member.CharacterId != characterId))
            {
                continue;
            }

            target = new InstanceReconnectTarget(instanceId, savedMapId,
                entranceX, entranceZ, kind);
            Console.WriteLine(
                "[instance] reconnect target resolved " +
                $"character={characterId} map={savedMapId} " +
                $"instance={instanceId} run={kind} " +
                $"entrance={entranceX:F2},{entranceZ:F2}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Joins the resolved instance exactly as an admitted member's own entry
    /// does, except that the object id is allocated for this new session.
    /// </summary>
    internal uint JoinPlayerReconnectedInstance(
        ClientSession session,
        int accountId,
        GameCharacter character,
        WorldInstanceId instanceId,
        bool worldReady = true,
        DateTimeOffset? joinedAt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        var runtime = GetRequiredWorldInstance(instanceId);
        if (runtime.MapId != character.CurrentMap ||
            runtime.Descriptor.LifecycleState !=
                WorldInstanceLifecycleState.Active)
        {
            throw new InvalidOperationException(
                "The reconnected world instance cannot accept this " +
                "character.");
        }

        return JoinWorldInstanceCore(
            session,
            accountId,
            character,
            requestedObjectId: null,
            runtime,
            worldReady,
            joinedAt);
    }

    private IEnumerable<(WorldInstanceId InstanceId, InstanceRunKind Kind)>
        CandidateInstanceReconnects()
    {
        foreach (var instanceId in _wonderlandAdmissions.Keys)
        {
            yield return (instanceId, InstanceRunKind.Wonderland);
        }
        foreach (var instanceId in _atlantisRunRosters.Keys)
        {
            yield return (instanceId, InstanceRunKind.Atlantis);
        }
        foreach (var instanceId in _harborAttackAdmissions.Keys)
        {
            yield return (instanceId, InstanceRunKind.HarborAttack);
        }
        foreach (var instanceId in _medusaLeaderUi.Keys)
        {
            yield return (instanceId, InstanceRunKind.Medusa);
        }
        foreach (var instanceId in TrainingGroundRunIds) // TRAINING
        {
            yield return (instanceId, InstanceRunKind.TrainingGround);
        }
    }

    /// <summary>
    /// Whether the run behind an instance is still running. A terminal run is
    /// never reconnected into: it is either counting its members down to the
    /// exit, or already retired.
    /// </summary>
    private bool IsInstanceReconnectRunActive(
        WorldInstanceRuntime runtime,
        InstanceRunKind kind) => kind switch
    {
        InstanceRunKind.Wonderland =>
            TryGetWonderlandEncounterSnapshot(runtime.InstanceId,
                out var wonderland) &&
            wonderland.State == WonderlandRunState.Active,
        InstanceRunKind.Atlantis =>
            TryGetAtlantisEncounterSnapshot(runtime.InstanceId,
                out var atlantis) &&
            atlantis.State == AtlantisRunState.Active,
        InstanceRunKind.HarborAttack =>
            _harborAttackAdmissions.TryGetValue(runtime.InstanceId,
                out var harbor) &&
            !_harborAttackTerminations.ContainsKey(runtime.InstanceId) &&
            DateTimeOffset.UtcNow < harbor.Deadline,
        InstanceRunKind.Medusa =>
            InvokeWorldOwner(runtime, static map =>
                map.TryGetMedusaOwnershipSnapshot(out var ownership) &&
                ownership.Run.State == MedusaRunState.Active),
        InstanceRunKind.TrainingGround => IsTrainingGroundReconnectActive(runtime.InstanceId), // TRAINING
        _ => false
    };

    /// <summary>
    /// Whether the character is still on the run's own admission record - the
    /// member who spent a daily attempt to be in this run.
    /// </summary>
    private bool IsInstanceReconnectMember(
        WorldInstanceRuntime runtime,
        InstanceRunKind kind,
        int accountId,
        int characterId) => kind switch
    {
        // 飘渺 records each confirmed entry, including a member admitted after
        // the party was sealed.
        InstanceRunKind.Wonderland =>
            _wonderlandAdmissions.TryGetValue(runtime.InstanceId,
                out var wonderland) &&
            wonderland.Entrants.Contains(characterId),
        // Atlantis keeps its admitted roster on the instance itself.
        InstanceRunKind.Atlantis =>
            InvokeWorldOwner(runtime, map =>
                map.IsAdmittedAtlantisMember(accountId, characterId)),
        InstanceRunKind.HarborAttack =>
            _harborAttackAdmissions.TryGetValue(runtime.InstanceId,
                out var harbor) &&
            harbor.Entrants.Contains(characterId),
        // Medusa binds the run's roster when the runtime is created, and keeps
        // it on the leader registration.
        InstanceRunKind.Medusa =>
            _medusaLeaderUi.TryGetValue(runtime.InstanceId,
                out var medusa) &&
            medusa.Roster.Any(entry => entry.CharacterId == characterId),
        InstanceRunKind.TrainingGround => // TRAINING
            IsTrainingGroundReconnectMember(runtime.InstanceId, characterId),
        _ => false
    };

    /// <summary>
    /// The instance caller's own arrival on the saved map - never the coordinate
    /// the character dropped at, so nobody logs back in inside a monster pack.
    /// </summary>
    private static bool TryResolveInstanceEntrance(
        byte savedMapId,
        out float entranceX,
        out float entranceZ)
    {
        switch (savedMapId)
        {
            case DynamicDungeonContentMapPolicy.WonderlandMapId:
                return TryResolveCallerArrival(
                    InstanceCallerProtocol.WonderlandClientSceneId,
                    out entranceX,
                    out entranceZ);
            case DynamicDungeonContentMapPolicy.AtlantisPortalMapId:
                return TryResolveCallerArrival(
                    InstanceCallerProtocol.AtlantisClientSceneId,
                    out entranceX,
                    out entranceZ);
            case DynamicDungeonContentMapPolicy.HarborAttackFirstMapId:
            case DynamicDungeonContentMapPolicy.HarborAttackSecondMapId:
                entranceX = InstanceCallerProtocol.HarborAttackArrivalX;
                entranceZ = InstanceCallerProtocol.HarborAttackArrivalZ;
                return true;
            case DynamicDungeonContentMapPolicy.TrainingGroundMapId: // TRAINING
                entranceX = InstanceCallerProtocol.HeraclesTrialArrivalX;
                entranceZ = InstanceCallerProtocol.HeraclesTrialArrivalZ;
                return true;
            case DynamicDungeonContentMapPolicy.WarriorTrainingGround1MapId: // WARRIOR (F-51)
            case DynamicDungeonContentMapPolicy.WarriorTrainingGround2MapId:
            case DynamicDungeonContentMapPolicy.WarriorTrainingGround3MapId:
            case DynamicDungeonContentMapPolicy.WarriorTrainingGround4MapId:
                entranceX = Godswar.Server.Application.WorldInstances.WarriorTrainingGroundPolicy.ArrivalX;
                entranceZ = Godswar.Server.Application.WorldInstances.WarriorTrainingGroundPolicy.ArrivalZ;
                return true;
            case DynamicDungeonContentMapPolicy.MedusaIslandMapId:
            case DynamicDungeonContentMapPolicy.MedusaIslandAlternateMapId:
                if (!MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                        "first-entry",
                        out var medusaEntrance))
                {
                    entranceX = 0f;
                    entranceZ = 0f;
                    return false;
                }
                entranceX = medusaEntrance.X;
                entranceZ = medusaEntrance.Z;
                return true;
            default:
                entranceX = 0f;
                entranceZ = 0f;
                return false;
        }
    }

    private static bool TryResolveCallerArrival(
        int clientSceneId,
        out float entranceX,
        out float entranceZ)
    {
        entranceX = 0f;
        entranceZ = 0f;
        if (!InstanceCallerProtocol.TryResolveInvitedInstance(clientSceneId,
                out var destination))
        {
            return false;
        }

        entranceX = destination.TargetX;
        entranceZ = destination.TargetZ;
        return true;
    }
}
