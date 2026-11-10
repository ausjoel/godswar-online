using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// 港湾遇袭 runs: one exact instance per admitted party, a thirty-minute window,
/// and a return to the faction capital when that window closes or the run ends.
/// </summary>
/// <remarks>
/// The dungeon's own monsters and drops are published into the map by the
/// content tooling, so a run owns no encounter, roster, loot or progression
/// state of its own. What it owns is admission, the clock, egress, the client's
/// repetition panel and retirement.
/// </remarks>
internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<WorldInstanceId, HarborAttackAdmission>
        _harborAttackAdmissions = [];
    private readonly ConcurrentDictionary<Guid, WorldInstanceId>
        _harborAttackReservations = [];
    // The instant the end control was accepted, or the deadline that expired, so
    // every non-completed end gives its members the same thirty-second leave
    // countdown a completed run shows.
    private readonly ConcurrentDictionary<WorldInstanceId, DateTimeOffset>
        _harborAttackTerminations = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _harborAttackRetirements = [];
    private readonly ConcurrentDictionary<WorldInstanceId, DateTimeOffset>
        _harborAttackLastError = [];
    private readonly ConcurrentDictionary<ClientSession, HarborAttackUiStamp>
        _harborAttackUi = [];
    private int _harborAttackWorldInFlight;

    internal bool TryStartHarborAttackEncounter(
        WorldInstanceId instanceId,
        ushort dailyLimit,
        IReadOnlyList<LegacyInstancePartyMember> members,
        DateTimeOffset startedAt,
        Guid reservationId)
    {
        if (dailyLimit == 0 ||
            reservationId == Guid.Empty ||
            members.Count is < 1 ||
            members.Count > Protocol.PartyProtocol.MaximumMembers ||
            !WorldInstances.TryFind(instanceId, out var runtime) ||
            !DynamicDungeonContentMapPolicy.IsHarborAttackMap(runtime.MapId))
        {
            return false;
        }

        lock (_gate)
        {
            if (_harborAttackAdmissions.ContainsKey(instanceId))
            {
                return false;
            }
            foreach (var member in members)
            {
                if (!_sessions.TryGetValue(member.Session, out var current) ||
                    current.CharacterId != member.CharacterId ||
                    current.AccountId != member.AccountId ||
                    current.Ownership != member.Ownership ||
                    current.RealmId != runtime.RealmId ||
                    current.Session.IsDisconnected ||
                    !IsCurrentAccountSession(
                        member.AccountId,
                        member.Session,
                        member.Ownership))
                {
                    return false;
                }
            }

            _harborAttackAdmissions[instanceId] = new(this, instanceId,
                reservationId,
                dailyLimit,
                members[0].Session,
                members.ToArray(),
                startedAt.ToUniversalTime());
            // 港湾's leader is the run's own mutable character id, the same one
            // every dungeon keeps in the shared per-run leader store.
            BeginInstanceRunLeader(instanceId, members[0].CharacterId);
            // The one per-run member record the roster publishes from opens here,
            // with the party this run was registered for.
            BeginInstanceRunMembership(instanceId, [.. members.Select(ToRosterEntry)]);
            _harborAttackReservations[reservationId] = instanceId;
            // INSTANCES: durable run record, so a server stop refunds the entry.
            OpenHarborAttackLedger(reservationId, runtime, startedAt,
                [.. members.Select(static member => member.CharacterId)]);
            return true;
        }
    }

    /// <summary>
    /// Whether this session has already been routed into a 港湾遇袭 run.
    /// </summary>
    /// <remarks>
    /// Deliberately weaker than <see cref="TryGetHarborAttackInstance"/>: the
    /// leader's own activation asks this before the destination readiness
    /// handshake, when the member is not yet a live occupant of the map.
    /// </remarks>
    internal bool IsHarborAttackRunForSession(ClientSession session)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(session, out var context) &&
                _harborAttackAdmissions.ContainsKey(context.WorldInstanceId);
        }
    }

    /// <summary>
    /// Whether this session currently holds a 港湾遇袭 run.
    /// </summary>
    internal bool TryGetHarborAttackInstance(
        ClientSession session,
        out WorldInstanceId instanceId)
    {
        instanceId = default;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                !_harborAttackAdmissions.ContainsKey(context.WorldInstanceId) ||
                !IsCurrentHarborAttackMember(context, context.WorldInstanceId))
            {
                return false;
            }
            instanceId = context.WorldInstanceId;
            return true;
        }
    }

    /// <summary>
    /// Free revival inside a 港湾遇袭 run: the character returns to the run's own
    /// arrival with a tenth of their health, and the run keeps its clock.
    /// </summary>
    internal bool TryReviveHarborAttackPlayer(
        ClientSession session,
        out WorldInstanceId instanceId,
        out float arrivalX,
        out float arrivalZ,
        out long lifeRevision)
    {
        instanceId = default;
        arrivalX = InstanceCallerProtocol.HarborAttackArrivalX;
        arrivalZ = InstanceCallerProtocol.HarborAttackArrivalZ;
        lifeRevision = -1;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                !_harborAttackAdmissions.TryGetValue(
                    context.WorldInstanceId,
                    out var admission) ||
                !IsCurrentHarborAttackMember(context, context.WorldInstanceId) ||
                DateTimeOffset.UtcNow >= admission.Deadline ||
                _harborAttackTerminations.ContainsKey(context.WorldInstanceId))
            {
                return false;
            }
            lock (context.Character.VitalsSync)
            {
                if (context.Character.CurrentHp > 0)
                {
                    return false;
                }
                lifeRevision = AdvancePlayerLifeRevision(session);
                if (lifeRevision < 0)
                {
                    return false;
                }
                context.Character.CurrentHp =
                    Math.Max(1, context.Character.MaxHp / 10);
                context.Character.CurrentMp =
                    Math.Max(0, context.Character.MaxMp / 10);
                context.Character.MarkVitalsChanged();
            }
            instanceId = context.WorldInstanceId;
            return true;
        }
    }

    /// <summary>
    /// The admitted leader ends the run through either native repetition control,
    /// exactly as Atlantis, Wonderland and Medusa do.
    /// </summary>
    internal bool TryTerminateHarborAttackRunFromLeader(
        ClientSession session,
        int repetitionId,
        int repetitionIndex)
    {
        if (repetitionIndex != 0)
        {
            return false;
        }
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var actor) ||
                actor.Character.CurrentMap != actor.MapId ||
                !DynamicDungeonContentMapPolicy.IsHarborAttackMap(actor.MapId) ||
                repetitionId != HarborAttackClientSceneId(actor.MapId) ||
                !_harborAttackAdmissions.TryGetValue(
                    actor.WorldInstanceId,
                    out var admission) ||
                actor.CharacterId != admission.LeaderId ||
                // Party leadership is a separate system and must not gate the
                // instance's own end control; only its leader may end it.
                !IsCurrentAccountSession(
                    actor.AccountId,
                    actor.Session,
                    actor.Ownership))
            {
                return false;
            }

            if (_harborAttackTerminations.TryAdd(
                    actor.WorldInstanceId,
                    DateTimeOffset.UtcNow))
            {
                // INSTANCES: durable now, not at the next tick.
                CloseHarborAttackLedger(admission.ReservationId, "terminated");
            }
            Console.WriteLine(
                "[harbor] leader termination instance=" +
                $"{actor.WorldInstanceId} leader={actor.Character.Name}");
            return true;
        }
    }

    private async Task AdvanceHarborAttackWorldAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _harborAttackWorldInFlight, 1) != 0)
        {
            return;
        }
        try
        {
            foreach (var entry in _harborAttackAdmissions)
            {
                var instanceId = entry.Key;
                try
                {
                    if (!WorldInstances.TryFind(instanceId, out var runtime))
                    {
                        ForgetHarborAttackRun(instanceId);
                        continue;
                    }

                    var admission = entry.Value;
                    if (runtime.Descriptor.LifecycleState !=
                        WorldInstanceLifecycleState.Active)
                    {
                        await TryRetireHarborAttackRuntimeAsync(
                            instanceId,
                            runtime,
                            cancellationToken);
                        continue;
                    }

                    GameSessionContext[] members;
                    lock (_gate)
                    {
                        members = SnapshotHarborAttackMembersLocked(runtime);
                        foreach (var member in members)
                        {
                            admission.Entrants.Add(member.CharacterId);
                        }
                        // A run whose registered leader has left keeps an ending
                        // authority: the earliest present entrant takes over,
                        // through the one leader-transfer implementation every
                        // dungeon shares.
                        MaintainInstanceRunLeader(
                            instanceId,
                            "HarborAttack",
                            [.. admission.OriginalMembers.Select(
                                static member => member.CharacterId)],
                            [.. members.Select(static member => member.CharacterId)]);
                    }

                    var expired = now >= admission.Deadline;
                    var terminated = _harborAttackTerminations.TryGetValue(
                        instanceId,
                        out var terminatedAt);
                    // INSTANCES: Christina's death completes the run (IGG acid=201).
                    if (!terminated)
                    {
                        DetectHarborAttackEndBossDeath(instanceId, runtime, now);
                    }
                    var completed = !terminated &&
                        TryGetHarborAttackCompletion(instanceId, out terminatedAt);
                    terminated |= completed;
                    if (expired || terminated)
                    {
                        CloseHarborAttackLedger(admission.ReservationId,
                            completed ? "completed" : terminated ? "terminated" : "deadline");
                        // A run that was ended or ran out of time enters the one
                        // shared terminal flow: the same four native frames, the
                        // same thirty-second countdown and the same clicker-only
                        // leave, and the rest are carried home when the thirty
                        // seconds expire.
                        var endStartedAt = terminated ? terminatedAt : admission.Deadline;
                        // BAY2: freeze the clearance EXP once, settle it now (retried by later ticks).
                        var rewardRun = FreezeHarborAttackReward(runtime, admission.ReservationId,
                            admission.StartedAt, members,
                            completed ? "completed" : terminated ? "terminated" : "deadline", endStartedAt);
                        var ending = BeginInstanceRunEnd(instanceId,
                            InstanceRunKind.HarborAttack, "HarborAttack",
                            runtime.MapId,
                            checked((ushort)HarborAttackClientSceneId(runtime.MapId)),
                            admission.DailyLimit, HarborAttackScore(instanceId),
                            endStartedAt);
                        if (members.Length != 0)
                        {
                            await PublishInstanceRunEndAsync(
                                ending,
                                [.. members.Select(ToInstanceRunEndMember)],
                                now,
                                settle: rewardRun is { } run // BAY2
                                    ? token => SettleHarborAttackRewardAsync(run, now, token)
                                    : null,
                                cancellationToken);
                        }
                        if (!IsInstanceRunEndWindowOpen(ending, now))
                        {
                            await ExitHarborAttackMembersAsync(
                                runtime,
                                admission,
                                members,
                                completed ? "completed" : terminated ? "terminated" : "deadline",
                                endStartedAt,
                                cancellationToken);
                        }
                    }
                    else
                    {
                        await PublishHarborAttackRunUiAsync(
                            runtime,
                            admission,
                            members,
                            now,
                            cancellationToken);
                    }

                    // The run is abandoned once everyone it admitted has left
                    // the map, but never before the first arrival: the runtime
                    // exists from before the leader's transfer commits. An
                    // instance whose whole party vanished before the first tick
                    // observed them is released when its own window closes.
                    if (members.Length == 0 &&
                        (admission.Entrants.Count != 0 || expired || terminated))
                    {
                        _harborAttackRetirements.TryAdd(instanceId, 0);
                    }
                    await TryRetireHarborAttackRuntimeAsync(
                        instanceId,
                        runtime,
                        cancellationToken);
                }
                catch (Exception error) when (
                    error is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
                {
                    LogHarborAttackDeferred(instanceId, now, error);
                }
            }
        }
        finally
        {
            // BAY2: owed rewards are retried even after their run is gone.
            await SettlePendingHarborAttackRewardsAsync(now, cancellationToken);
            Volatile.Write(ref _harborAttackWorldInFlight, 0);
        }
    }

    private async Task PublishHarborAttackRunUiAsync(
        WorldInstanceRuntime runtime,
        HarborAttackAdmission admission,
        IReadOnlyList<GameSessionContext> members,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (members.Count == 0)
        {
            return;
        }

        var sceneId = HarborAttackClientSceneId(runtime.MapId);
        var remaining = checked((int)Math.Clamp(
            Math.Ceiling((admission.Deadline - now).TotalSeconds),
            0,
            HarborAttackPolicy.TimeLimitSeconds));
        var roster = SnapshotInstanceRoster(
            runtime.InstanceId, InstanceRunKind.HarborAttack, now);
        var signature = InstanceRosterSignature(roster);
        var score = HarborAttackScore(runtime.InstanceId); // INSTANCES
        var stamp = new HarborAttackUiStamp(
            runtime.InstanceId,
            remaining,
            signature,
            Score: score);

        foreach (var member in members)
        {
            // The repetition state must reach the client after its destination
            // scene is ready, exactly as the Wonderland and Medusa panels do: a
            // sync delivered while the scene is still loading leaves the panel
            // without its run state (and without the native end-instance
            // control). A member who is not ready yet is published on a later
            // tick.
            if (!member.WorldReady)
            {
                continue;
            }
            Task? write = null;
            lock (_gate)
            {
                if (!IsCurrentHarborAttackMember(member, runtime.InstanceId))
                {
                    continue;
                }
                _harborAttackUi.TryGetValue(member.Session, out var previous);
                if (previous == stamp)
                {
                    continue;
                }

                var packets = new List<ReadOnlyMemory<byte>>();
                if (previous?.InstanceId != stamp.InstanceId)
                {
                    // Repetition state 5 is the client's active-run state, the
                    // same value the Wonderland panel reasserts.
                    packets.Add(PacketBuilder.RepetitionSync(
                        checked((ushort)sceneId),
                        0,
                        0,
                        5,
                        admission.DailyLimit));
                }
                if (previous?.InstanceId != stamp.InstanceId ||
                    previous.Roster != signature)
                {
                    packets.Add(PacketBuilder.RepetitionInstanceMembers(roster));
                }
                packets.Add(PacketBuilder.RepetitionFightInfo(remaining, score));
                if (member.Session.TryAdmitExactBatch(packets, out var admitted))
                {
                    _harborAttackUi[member.Session] = stamp;
                }
                write = admitted;
            }

            if (write is not null)
            {
                try
                {
                    await write;
                }
                catch (Exception error) when (
                    error is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
                {
                    member.Session.Disconnect();
                }
            }
        }
    }

    private async Task ExitHarborAttackMembersAsync(
        WorldInstanceRuntime runtime,
        HarborAttackAdmission admission,
        IReadOnlyList<GameSessionContext> members,
        string reason,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        if (members.Count == 0)
        {
            return;
        }

        // The one automatic exit every dungeon shares: the countdown expired, so
        // whoever is still inside is carried home. A member who pressed leave
        // during the countdown left on his own, through the shared clicker-only
        // leave.
        var transferred = await EgressInstanceRunEndAsync(
            BeginInstanceRunEnd(runtime.InstanceId, InstanceRunKind.HarborAttack,
                "HarborAttack", runtime.MapId,
                checked((ushort)HarborAttackClientSceneId(runtime.MapId)),
                admission.DailyLimit, HarborAttackScore(runtime.InstanceId), endedAt),
            [.. members.Select(ToInstanceRunEndMember)],
            TransitionPartyMemberToAuthoritativeInstanceAsync,
            cancellationToken);
        Console.WriteLine(
            "[harbor] exit instance=" + runtime.InstanceId +
            $" reason={reason} leader={admission.LeaderId} " +
            $"transferred={transferred}");
    }

    private async Task TryRetireHarborAttackRuntimeAsync(
        WorldInstanceId instanceId,
        WorldInstanceRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!_harborAttackRetirements.ContainsKey(instanceId))
        {
            return;
        }

        for (var phase = 0; phase < 3; phase++)
        {
            if (!WorldInstances.TryFind(instanceId, out runtime))
            {
                ForgetHarborAttackRun(instanceId);
                return;
            }

            var descriptor = runtime.Descriptor;
            bool empty;
            if (descriptor.LifecycleState == WorldInstanceLifecycleState.Closed)
            {
                empty = runtime.Map.Population == 0;
            }
            else
            {
                if (runtime.Owner.GetSnapshot().State !=
                    SingleOwnerMailboxState.Accepting)
                {
                    return;
                }
                empty = InvokeWorldOwner(
                    runtime,
                    map => map.Population == 0);
            }
            if (!empty)
            {
                return;
            }

            var at = Maximum(DateTimeOffset.UtcNow, descriptor.LastTransitionAt);
            var result = descriptor.LifecycleState switch
            {
                WorldInstanceLifecycleState.Active =>
                    await WorldInstances.BeginDrainAsync(
                        instanceId,
                        descriptor.Revision,
                        at,
                        cancellationToken),
                WorldInstanceLifecycleState.Draining =>
                    await WorldInstances.CloseAsync(
                        instanceId,
                        descriptor.Revision,
                        at,
                        cancellationToken),
                WorldInstanceLifecycleState.Closed =>
                    await WorldInstances.RemoveClosedAsync(
                        instanceId,
                        cancellationToken),
                _ => default
            };
            if (result.Status is
                WorldInstanceRuntimeDirectoryStatus.Removed or
                WorldInstanceRuntimeDirectoryStatus.InstanceNotFound)
            {
                ForgetHarborAttackRun(instanceId);
                return;
            }
            if (result.Status is not (
                WorldInstanceRuntimeDirectoryStatus.Draining or
                WorldInstanceRuntimeDirectoryStatus.Closed))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The sessions this run has routed into its instance.
    /// </summary>
    /// <remarks>
    /// Deliberately not gated on the client's destination readiness handshake:
    /// the abort vote and the terminal egress must account for, and carry out,
    /// every member the run admitted even if their client has not finished
    /// loading the scene yet.
    /// </remarks>
    private GameSessionContext[] SnapshotHarborAttackMembersLocked(
        WorldInstanceRuntime runtime) =>
        _sessions.Values
            .Where(context =>
                context.WorldInstanceId == runtime.InstanceId &&
                !context.Session.IsDisconnected &&
                context.RealmId == runtime.RealmId &&
                context.Ownership.IsValid &&
                IsCurrentAccountSession(
                    context.AccountId,
                    context.Session,
                    context.Ownership))
            .OrderBy(context => context.CharacterId)
            .ToArray();

    private bool IsCurrentHarborAttackMember(
        GameSessionContext member,
        WorldInstanceId instanceId) =>
        _sessions.TryGetValue(member.Session, out var current) &&
        !current.Session.IsDisconnected &&
        current.AccountId == member.AccountId &&
        current.CharacterId == member.CharacterId &&
        current.Ownership == member.Ownership &&
        current.WorldInstanceId == instanceId &&
        IsCurrentAccountSession(
            member.AccountId,
            member.Session,
            member.Ownership);

    private void ForgetHarborAttackRun(WorldInstanceId instanceId)
    {
        if (_harborAttackAdmissions.TryRemove(instanceId, out var admission))
        {
            CloseHarborAttackLedger(admission.ReservationId, "abandoned");
            _harborAttackReservations.TryRemove(
                admission.ReservationId,
                out _);
            CloseMemberEntryWindow(admission.LeaderSession);
        }
        ForgetInstanceRunEnd(instanceId);
        _harborAttackTerminations.TryRemove(instanceId, out _);
        _harborAttackRetirements.TryRemove(instanceId, out _);
        _harborAttackLastError.TryRemove(instanceId, out _);
        ForgetHarborAttackProgress(instanceId); // INSTANCES
        ForgetHarborAttackRewardRun(instanceId); // BAY2
        ForgetInstanceRunMembership(instanceId);
        foreach (var cached in _harborAttackUi
                     .Where(pair => pair.Value.InstanceId == instanceId))
        {
            _harborAttackUi.TryRemove(cached);
        }
    }

    private static int HarborAttackClientSceneId(int mapId) =>
        mapId == DynamicDungeonContentMapPolicy.HarborAttackSecondMapId
            ? InstanceCallerProtocol.HarborAttackSecondClientSceneId
            : InstanceCallerProtocol.HarborAttackFirstClientSceneId;

    private void LogHarborAttackDeferred(
        WorldInstanceId instanceId,
        DateTimeOffset now,
        Exception error)
    {
        if (_harborAttackLastError.TryAdd(instanceId, now) ||
            _harborAttackLastError.TryGetValue(instanceId, out var previous) &&
            now - previous >= TimeSpan.FromSeconds(30) &&
            _harborAttackLastError.TryUpdate(instanceId, now, previous))
        {
            Console.Error.WriteLine(
                $"[harbor] deferred instance={instanceId}: {error.Message}");
        }
    }

    private sealed class HarborAttackAdmission(
        GameSessionRegistry owner,
        WorldInstanceId instanceId,
        Guid reservationId,
        ushort dailyLimit,
        ClientSession leaderSession,
        LegacyInstancePartyMember[] originalMembers,
        DateTimeOffset startedAt)
    {
        public Guid ReservationId { get; } = reservationId;
        public ushort DailyLimit { get; } = dailyLimit;
        /// <summary>
        /// The run's one leader identity: the shared per-run leader store, read
        /// here because the shared roster implementation publishes from this
        /// record. Transferable, and written only through the shared transfer.
        /// </summary>
        public int LeaderId => owner.InstanceRunLeaderCharacterId(instanceId);
        public ClientSession LeaderSession { get; } = leaderSession;
        public LegacyInstancePartyMember[] OriginalMembers { get; } =
            originalMembers;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public HashSet<int> Entrants { get; } = [];

        public DateTimeOffset Deadline =>
            StartedAt + HarborAttackPolicy.TimeLimit;
    }

    private sealed record HarborAttackUiStamp(
        WorldInstanceId InstanceId,
        int RemainingSeconds,
        string Roster,
        bool Ending = false,
        int Score = 0);
}

