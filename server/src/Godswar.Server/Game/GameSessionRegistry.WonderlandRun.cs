using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const byte WonderlandMapId = 207;
    private const ushort WonderlandClientSceneId = 227;
    private readonly ConcurrentDictionary<WorldInstanceId, WonderlandAdmission> _wonderlandAdmissions = [];
    private readonly ConcurrentDictionary<Guid, WorldInstanceId> _wonderlandReservations = [];

    internal bool TryStartWonderlandEncounter(WorldInstanceId instanceId, ushort dailyLimit,
        IReadOnlyList<LegacyInstancePartyMember> members, DateTimeOffset startedAt, Guid reservationId)
    {
        if (dailyLimit == 0 || reservationId == Guid.Empty || members.Count is < 1 or > 5 ||
            !WorldInstances.TryFind(instanceId, out var runtime) || runtime.MapId != WonderlandMapId)
            return false;
        lock (_gate)
        {
            if (_wonderlandAdmissions.ContainsKey(instanceId)) return false;
            var participants = new List<WonderlandParticipant>();
            foreach (var member in members)
            {
                if (!_sessions.TryGetValue(member.Session, out var current) ||
                    current.CharacterId != member.CharacterId || current.AccountId != member.AccountId ||
                    current.Ownership != member.Ownership || current.RealmId != runtime.RealmId ||
                    current.Session.IsDisconnected || !IsCurrentAccountSession(
                        member.AccountId, member.Session, member.Ownership)) return false;
                participants.Add(new(member.CharacterId, member.Level, current.Character.Camp));
            }
            if (!InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
                map.TryConfigureWonderland(_gameplayCatalogs.Content, participants, startedAt))) return false;
            _wonderlandAdmissions[instanceId] = new(this, instanceId, reservationId, dailyLimit,
                members.ToArray(), startedAt.ToUniversalTime());
            // 飘渺's leader is the run's own mutable character id, the same one
            // every dungeon keeps in the shared per-run leader store.
            BeginInstanceRunLeader(instanceId, members[0].CharacterId);
            // The one per-run member record the roster publishes from opens here,
            // with the party this run was registered for.
            BeginInstanceRunMembership(instanceId, [.. members.Select(ToRosterEntry)]);
            _wonderlandReservations[reservationId] = instanceId;
            return true;
        }
    }

    internal void RecordWonderlandAdmissions(Guid reservationId, IReadOnlyCollection<int> admittedIds)
    {
        lock (_gate)
        {
            if (!_wonderlandReservations.TryGetValue(reservationId, out var instanceId) ||
                !_wonderlandAdmissions.TryGetValue(instanceId, out var admission) || admission.Sealed) return;
            foreach (var id in admittedIds)
                if (admission.OriginalMembers.Any(member => member.CharacterId == id)) admission.Entrants.Add(id);
        }
    }

    internal void CompleteWonderlandAdmissions(Guid reservationId)
    {
        lock (_gate)
        {
            if (!_wonderlandReservations.TryGetValue(reservationId, out var instanceId) ||
                !_wonderlandAdmissions.TryGetValue(instanceId, out var admission) || admission.Sealed) return;
            // Include a committed entry whose transport acknowledgement threw
            // before the handler could publish its normal admission marker.
            foreach (var member in admission.OriginalMembers)
                if (_sessions.TryGetValue(member.Session, out var current) &&
                    current.CharacterId == member.CharacterId && current.AccountId == member.AccountId &&
                    current.Ownership == member.Ownership && current.WorldInstanceId == instanceId)
                    admission.Entrants.Add(member.CharacterId);
            if (WorldInstances.TryFind(instanceId, out var runtime))
                InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
                {
                    if (admission.Entrants.Count == 0 ||
                        !map.TryFinalizeWonderlandAdmissions(admission.Entrants))
                        map.CancelWonderland(DateTimeOffset.UtcNow);
                    return true;
                });
            admission.Sealed = true;
            if (admission.Entrants.Count == 0) _wonderlandDepartures.TryAdd(instanceId, 0);
        }
    }

    /// <summary>
    /// Admits a member who confirmed the party's window after the run was sealed,
    /// so 飘渺 accepts a slower teammate exactly as the other instances do.
    /// </summary>
    /// <remarks>
    /// Registry-first lock order: the registry gate is held before the owning map
    /// is invoked, and the map never calls back into the registry.
    /// </remarks>
    internal bool TryAdmitWonderlandLateEntrant(
        WorldInstanceId instanceId,
        ClientSession session,
        int characterId,
        int level,
        byte camp,
        Guid reservationId = default)
    {
        lock (_gate)
        {
            if (!_wonderlandAdmissions.TryGetValue(instanceId, out var admission))
            {
                Console.WriteLine(
                    $"[instance-invite] late admission refused: no admission instance={instanceId}");
                return false;
            }
            if (!admission.Sealed)
            {
                Console.WriteLine(
                    $"[instance-invite] late admission refused: not sealed instance={instanceId}");
                return false;
            }
            if (admission.Entrants.Contains(characterId))
            {
                // Idempotent: a repeated confirmation (the client may answer the
                // same window twice) must continue to the transfer instead of
                // releasing the attempt the first confirmation already spent.
                Console.WriteLine(
                    $"[instance-invite] late admission already recorded character={characterId}");
                return true;
            }
            if (!_sessions.TryGetValue(session, out var context) ||
                context.CharacterId != characterId ||
                context.WorldInstanceId == instanceId)
            {
                Console.WriteLine(
                    $"[instance-invite] late admission refused: session mismatch character={characterId}");
                return false;
            }
            if (!WorldInstances.TryFind(instanceId, out var runtime) ||
                runtime.MapId != WonderlandMapId)
            {
                Console.WriteLine(
                    $"[instance-invite] late admission refused: runtime missing instance={instanceId}");
                return false;
            }
            if (!InvokeWorldOwnerAuthoritativeMutation(
                    runtime,
                    map => map.TryAppendWonderlandParticipant(
                        characterId,
                        level,
                        camp,
                        out _)))
            {
                Console.WriteLine(
                    "[instance-invite] late admission refused by the run " +
                    $"instance={instanceId} character={characterId} camp={camp} " +
                    $"level={level}");
                return false;
            }
            admission.Entrants.Add(characterId);
            // Keep the invited member's own identity on the run's roster: the
            // title settlement fences its recipients against the admitted roster,
            // and this member holds no seat in the registered party.
            admission.JoinedMembers.Add(new LegacyInstancePartyMember(
                context.Session,
                context.AccountId,
                context.CharacterId,
                context.CharacterName,
                context.Character.Level,
                context.Character.Profession,
                context.RealmId,
                context.WorldInstanceId,
                context.MapId,
                context.Ownership));
            if (reservationId != Guid.Empty)
            {
                _wonderlandReservations[reservationId] = instanceId;
            }
            Console.WriteLine(
                "[instance-invite] late admission accepted " +
                $"instance={instanceId} character={characterId}");
            return true;
        }
    }

    private bool MayEnterWonderlandMap(MapInstance map, int characterId)
    {
        if (map.MapId != WonderlandMapId || !map.TryGetWonderlandSnapshot(out var run)) return true;
        if (!_wonderlandAdmissions.TryGetValue(map.WorldInstanceId, out var admission)) return false;
        if (run.State == WonderlandRunState.Active)
            return admission.Sealed ? admission.Entrants.Contains(characterId) :
                admission.OriginalMembers.Any(member => member.CharacterId == characterId);
        // A completed run permits only in-place travel for an existing owner.
        // WorldReady can be false while that same-map scene change is pending.
        return WonderlandCompletionPolicy.IsTreasureWindowOpen(run, DateTimeOffset.UtcNow) &&
            admission.Sealed && admission.Entrants.Contains(characterId) &&
            _sessions.Values.Any(member => member.CharacterId == characterId &&
                member.WorldInstanceId == map.WorldInstanceId && member.MapId == WonderlandMapId &&
                member.Character.CurrentMap == WonderlandMapId && !member.Session.IsDisconnected &&
                member.Ownership.IsValid && IsCurrentAccountSession(member.AccountId, member.Session, member.Ownership));
    }

    internal bool TryGetWonderlandEncounterSnapshot(WorldInstanceId instanceId, out WonderlandSnapshot snapshot)
    {
        snapshot = null!;
        if (!WorldInstances.TryFind(instanceId, out var runtime) || runtime.MapId != WonderlandMapId) return false;
        var captured = InvokeWorldOwner(runtime, map => map.TryGetWonderlandSnapshot(out var run) ? run : null);
        if (captured is null) return false;
        snapshot = captured;
        return true;
    }

    internal void RecordWonderlandMonsterKillCommitted(WorldInstanceRuntime runtime,
        MonsterDamageResult damage, DateTimeOffset now)
    {
        if (runtime.MapId != WonderlandMapId || !damage.Killed || damage.AfterHealth != 0 ||
            damage.Monster.Definition.MapId != WonderlandMapId || damage.HealthMutation is not { } mutation ||
            mutation.AfterHealthRevision != damage.Monster.HealthRevision ||
            mutation.SpawnGeneration != damage.Monster.SpawnGeneration) return;
        lock (_gate)
        {
            if (!_wonderlandAdmissions.TryGetValue(runtime.InstanceId, out var admission) || !admission.Sealed) return;
            var result = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
            {
                if (!map.TryGetMonsterSnapshot(damage.ObjectId, out var current) || current.IsAlive ||
                    current.CurrentHealth != 0 || current.RuntimeInstanceId != damage.Monster.RuntimeInstanceId ||
                    current.SpawnGeneration != damage.Monster.SpawnGeneration ||
                    current.HealthRevision != mutation.AfterHealthRevision) return null;
                return map.RecordCommittedWonderlandKill(runtime.InstanceId, damage.ObjectId,
                    damage.Monster.SpawnGeneration, now);
            });
            if (result?.ClearedIsland is not { } clear || clear.Island is < 1 or > 8) return;
            // The one line an operator needs to see that scoring is live: the
            // cleared-island count is both the panel's score and the score the
            // island teleporters gate on.
            Console.WriteLine($"[wonderland] score instance={runtime.InstanceId} island={clear.Island} " +
                $"cleared={result.Snapshot.CompletedIslands} remaining={result.Snapshot.RequiredMonstersRemaining} " +
                $"outcome={result.Outcome} state={result.Snapshot.State}");
            // The run's admitted roster is the registered party plus every member
            // admitted after it was sealed. It fences the recipients (see below)
            // and it is what the durable title store reconciles against the daily
            // entry ledger, across each of those admissions' own reservations.
            var roster = admission.OriginalMembers
                .Concat(admission.JoinedMembers)
                .Where(member => admission.Entrants.Contains(member.CharacterId))
                .GroupBy(member => member.CharacterId)
                .Select(group => group.First())
                .OrderBy(member => member.CharacterId)
                .ToArray();
            var admitted = roster
                .Select(member => new WonderlandTitleMember(member.AccountId, member.CharacterId, member.Ownership))
                .ToArray();
            // Title recipients are every admitted member still inside when the
            // island clears, whether they registered with the party or joined it
            // later. A character who left or dropped is not present and is not
            // paid.
            var finishers = SnapshotWonderlandMembersLocked(runtime).Where(context =>
                    admission.Entrants.Contains(context.CharacterId) &&
                    roster.Any(member =>
                        member.CharacterId == context.CharacterId &&
                        member.AccountId == context.AccountId)).ToArray();
            var frozen = finishers
                .Select(context => new WonderlandTitleMember(context.AccountId, context.CharacterId, context.Ownership))
                .ToArray();
            if (admitted.Length == 0 || frozen.Length == 0) return;
            // Every admission reservation that put a character into this run,
            // including the ones a later member claimed for themselves.
            var reservations = _wonderlandReservations
                .Where(pair => pair.Value == runtime.InstanceId)
                .Select(pair => pair.Key)
                .Append(admission.ReservationId)
                .Distinct()
                .Order()
                .ToArray();
            var request = new WonderlandTitleRequest(runtime.InstanceId, runtime.RealmId, admission.ReservationId,
                admission.StartedAt, clear.ClearedAt, clear.Island, admitted, frozen, reservations);
            if (QueueWonderlandTitleMilestone(request))
                CaptureWonderlandCompletionNoticeLocked(request, admission.LeaderId, finishers);
        }
    }

    private GameSessionContext[] SnapshotWonderlandMembersLocked(WorldInstanceRuntime runtime) =>
        _sessions.Values.Where(context => context.WorldInstanceId == runtime.InstanceId &&
            context.WorldReady && !context.Session.IsDisconnected && context.RealmId == runtime.RealmId &&
            context.MapId == WonderlandMapId && context.Character.CurrentMap == WonderlandMapId &&
            context.Ownership.IsValid && IsCurrentAccountSession(context.AccountId, context.Session, context.Ownership))
            .OrderBy(context => context.CharacterId).ToArray();

    /// <summary>
    /// Moves 飘渺's leader to the earliest still-present participant when the
    /// registered leader has left the run, through the one leader-transfer
    /// implementation every dungeon shares. Called from the tick under the
    /// registry gate; the run's own end control follows the new leader.
    /// </summary>
    private void MaintainWonderlandLeaderLocked(
        WorldInstanceId instanceId,
        WonderlandSnapshot run,
        GameSessionContext[] members) =>
        MaintainInstanceRunLeader(
            instanceId,
            "Wonderland",
            [.. run.Participants.Select(static participant => participant.CharacterId)],
            [.. members.Select(static member => member.CharacterId)]);

    private bool IsCurrentWonderlandMember(GameSessionContext member, WorldInstanceId instanceId) =>
        _sessions.TryGetValue(member.Session, out var current) && current.WorldReady &&
        !current.Session.IsDisconnected && current.AccountId == member.AccountId &&
        current.CharacterId == member.CharacterId && current.Ownership == member.Ownership &&
        current.WorldInstanceId == instanceId && current.MapId == WonderlandMapId &&
        current.Character.CurrentMap == WonderlandMapId &&
        IsCurrentAccountSession(member.AccountId, member.Session, member.Ownership);

    private sealed class WonderlandAdmission(GameSessionRegistry owner, WorldInstanceId instanceId,
        Guid reservationId, ushort dailyLimit,
        LegacyInstancePartyMember[] originalMembers, DateTimeOffset startedAt)
    {
        public Guid ReservationId { get; } = reservationId;
        public ushort DailyLimit { get; } = dailyLimit;
        /// <summary>
        /// The run's one leader identity: the shared per-run leader store, read
        /// here because the shared roster implementation publishes from this
        /// record. Transferable, and written only through the shared transfer.
        /// </summary>
        public int LeaderId => owner.InstanceRunLeaderCharacterId(instanceId);
        public LegacyInstancePartyMember[] OriginalMembers { get; } = originalMembers;

        /// <summary>
        /// Members admitted after the registered party was sealed - invited by
        /// name, or confirming the party window after the leader entered. They
        /// hold their own admission reservation and belong to the run's roster for
        /// entitlement, without a seat in the registered party.
        /// </summary>
        public List<LegacyInstancePartyMember> JoinedMembers { get; } = [];
        public DateTimeOffset StartedAt { get; } = startedAt;
        public HashSet<int> Entrants { get; } = [];
        public bool Sealed { get; set; }
    }
}
