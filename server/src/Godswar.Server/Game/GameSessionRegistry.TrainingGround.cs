using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// TRAINING: Training Ground (Trials of Heracles) runs on map 210 - one exact
/// instance per admitted party, a thirty-minute window, 300 points (elite 10,
/// normal 1) to clear, a return to the faction capital when the run ends, and
/// one completion reward per member present at the end. WARRIOR (F-51): the same
/// run also hosts the four Warriors' Training Grounds (maps 201/202/203/206); every
/// map-specific value comes from the run's TrainingGroundDefinition.
/// </summary>
/// <remarks>
/// A deliberate twin of the 港湾遇袭 run (GameSessionRegistry.HarborAttack.cs)
/// kept in its own file and its own state, so Bay Under Attack can change
/// without touching it. It shares the one end-of-run flow, roster, leader store,
/// member Enter window and capital egress every dungeon uses.
/// </remarks>
internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<WorldInstanceId, TrainingGroundAdmission>
        _trainingAdmissions = [];
    private readonly ConcurrentDictionary<WorldInstanceId, DateTimeOffset>
        _trainingTerminations = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _trainingRetirements = [];
    private readonly ConcurrentDictionary<WorldInstanceId, DateTimeOffset>
        _trainingLastError = [];
    private readonly ConcurrentDictionary<ClientSession, TrainingGroundUiStamp>
        _trainingUi = [];
    private readonly ConcurrentDictionary<WorldInstanceId, TrainingGroundRunProgress>
        _trainingProgress = [];
    private int _trainingWorldInFlight;
    private ITrainingGroundStore? _trainingStore;

    internal void ConfigureTrainingGround(ITrainingGroundStore? store)
    {
        if (store is not null)
        {
            Interlocked.CompareExchange(ref _trainingStore, store, null);
        }
    }

    internal ITrainingGroundStore? TrainingGroundStore => Volatile.Read(ref _trainingStore);

    internal bool TryStartTrainingGroundEncounter(
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
            !TrainingGroundPolicy.TryGetGround(runtime.MapId, out var ground)) // WARRIOR (F-51)
        {
            return false;
        }

        lock (_gate)
        {
            if (_trainingAdmissions.ContainsKey(instanceId))
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

            _trainingAdmissions[instanceId] = new(this, instanceId,
                reservationId,
                dailyLimit,
                members[0].Session,
                members.ToArray(),
                startedAt.ToUniversalTime(),
                checked((short)runtime.RealmId.Value),
                ground);
            BeginInstanceRunLeader(instanceId, members[0].CharacterId);
            BeginInstanceRunMembership(instanceId, [.. members.Select(ToRosterEntry)]);
            Console.WriteLine(
                $"[training] run started ground={ground.Key} instance={instanceId} reservation={reservationId} " +
                $"members={string.Join(',', members.Select(static member => member.CharacterId))}");
            return true;
        }
    }

    internal bool IsTrainingGroundAdmission(WorldInstanceId instanceId) =>
        _trainingAdmissions.ContainsKey(instanceId);

    /// <summary>Whether this session currently holds a Training Ground run.</summary>
    internal bool TryGetTrainingGroundInstance(
        ClientSession session,
        out WorldInstanceId instanceId)
    {
        instanceId = default;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                !_trainingAdmissions.ContainsKey(context.WorldInstanceId) ||
                !IsCurrentTrainingGroundMember(context, context.WorldInstanceId))
            {
                return false;
            }
            instanceId = context.WorldInstanceId;
            return true;
        }
    }

    /// <summary>
    /// Free revival inside a run (IGG: "revive near the entrance"): back to the
    /// arrival with a tenth of health and mana; the run keeps its clock.
    /// </summary>
    internal bool TryReviveTrainingGroundPlayer(
        ClientSession session,
        out WorldInstanceId instanceId,
        out long lifeRevision)
    {
        instanceId = default;
        lifeRevision = -1;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                !_trainingAdmissions.TryGetValue(
                    context.WorldInstanceId,
                    out var admission) ||
                !IsCurrentTrainingGroundMember(context, context.WorldInstanceId) ||
                DateTimeOffset.UtcNow >= admission.Deadline ||
                _trainingTerminations.ContainsKey(context.WorldInstanceId) ||
                TryGetTrainingGroundCompletion(context.WorldInstanceId, out _))
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
    /// The run's leader ends it through the native repetition control (IGG: a
    /// leader-initiated termination; rewards by the level of completion).
    /// </summary>
    internal bool TryTerminateTrainingGroundRunFromLeader(
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
                !TrainingGroundPolicy.TryGetGround(actor.MapId, out var ground) ||
                repetitionId != ground.ClientSceneId || // WARRIOR (F-51): this ground's own scene
                !_trainingAdmissions.TryGetValue(
                    actor.WorldInstanceId,
                    out var admission) ||
                actor.CharacterId != admission.LeaderId ||
                !IsCurrentAccountSession(
                    actor.AccountId,
                    actor.Session,
                    actor.Ownership))
            {
                return false;
            }

            _trainingTerminations.TryAdd(actor.WorldInstanceId, DateTimeOffset.UtcNow);
            Console.WriteLine(
                "[training] leader termination instance=" +
                $"{actor.WorldInstanceId} leader={actor.Character.Name} " +
                $"score={TrainingGroundScore(actor.WorldInstanceId)}");
            return true;
        }
    }

    /// <summary>
    /// The kill recorder for a monster that died inside a Training Ground run, or
    /// null for every other map. Invoked once the reward settlement committed; a
    /// replayed settlement records nothing twice (object id + spawn generation).
    /// </summary>
    internal Action? CaptureTrainingGroundMonsterKill(
        ClientSession session,
        MonsterDamageResult damage)
    {
        if (!damage.Killed ||
            !TrainingGroundPolicy.IsTrainingGroundMap(damage.Monster.Definition.MapId) ||
            !_sessions.TryGetValue(session, out var context) ||
            context.MapId != damage.Monster.Definition.MapId ||
            !_trainingAdmissions.ContainsKey(context.WorldInstanceId))
        {
            return null;
        }

        var instanceId = context.WorldInstanceId;
        var objectId = damage.ObjectId;
        var generation = damage.Monster.SpawnGeneration;
        var templateKey = damage.Monster.Definition.TemplateKey;
        return () => RecordTrainingGroundKill(
            instanceId, objectId, generation, templateKey, DateTimeOffset.UtcNow);
    }

    internal void RecordTrainingGroundKill(
        WorldInstanceId instanceId,
        uint objectId,
        uint spawnGeneration,
        string templateKey,
        DateTimeOffset at)
    {
        if (!_trainingAdmissions.TryGetValue(instanceId, out var admission) ||
            _trainingTerminations.ContainsKey(instanceId) ||
            at >= admission.Deadline)
        {
            return;
        }

        CompleteWarriorScrollTasks(instanceId, admission.Ground.MapId, templateKey); // WARRIOR scrolls (F-146 b)
        var progress = _trainingProgress.GetOrAdd(instanceId, _ => new(admission.Ground)); // WARRIOR (F-51)
        TrainingGroundKillOutcome outcome;
        int score;
        lock (progress)
        {
            outcome = progress.Record(objectId, spawnGeneration, templateKey, at);
            score = progress.Score;
        }
        if (outcome == TrainingGroundKillOutcome.Ignored)
        {
            return;
        }
        Console.WriteLine(
            $"[training] kill instance={instanceId} object={objectId} " +
            $"template={templateKey} score={score}" +
            (outcome == TrainingGroundKillOutcome.Completed
                ? $" cleared={admission.Ground.TargetScore}" : string.Empty) +
            (WarriorTrainingGroundPolicy.IsMagmaScorpion(templateKey) ? " magma-scorpion" : string.Empty));
    }

    internal int TrainingGroundScore(WorldInstanceId instanceId)
    {
        if (!_trainingProgress.TryGetValue(instanceId, out var progress))
        {
            return 0;
        }
        lock (progress)
        {
            return progress.Score;
        }
    }

    private bool TryGetTrainingGroundCompletion(
        WorldInstanceId instanceId,
        out DateTimeOffset completedAt)
    {
        completedAt = default;
        if (!_trainingProgress.TryGetValue(instanceId, out var progress))
        {
            return false;
        }
        lock (progress)
        {
            if (progress.CompletedAt is not { } at)
            {
                return false;
            }
            completedAt = at;
            return true;
        }
    }

    private async Task AdvanceTrainingGroundWorldAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _trainingWorldInFlight, 1) != 0)
        {
            return;
        }
        try
        {
            foreach (var entry in _trainingAdmissions)
            {
                var instanceId = entry.Key;
                try
                {
                    if (!WorldInstances.TryFind(instanceId, out var runtime))
                    {
                        ForgetTrainingGroundRun(instanceId);
                        continue;
                    }

                    var admission = entry.Value;
                    if (runtime.Descriptor.LifecycleState !=
                        WorldInstanceLifecycleState.Active)
                    {
                        await TryRetireTrainingGroundRuntimeAsync(
                            instanceId,
                            cancellationToken);
                        continue;
                    }

                    GameSessionContext[] members;
                    lock (_gate)
                    {
                        members = SnapshotTrainingGroundMembersLocked(runtime);
                        foreach (var member in members)
                        {
                            admission.Entrants.Add(member.CharacterId);
                        }
                        MaintainInstanceRunLeader(
                            instanceId,
                            "TrainingGround",
                            [.. admission.OriginalMembers.Select(
                                static member => member.CharacterId)],
                            [.. members.Select(static member => member.CharacterId)]);
                    }

                    var completed = TryGetTrainingGroundCompletion(instanceId, out var completedAt);
                    var terminatedAt = default(DateTimeOffset);
                    var terminated = !completed &&
                        _trainingTerminations.TryGetValue(instanceId, out terminatedAt);
                    var expired = !completed && !terminated && now >= admission.Deadline;
                    if (completed || terminated || expired)
                    {
                        var endStartedAt = completed ? completedAt
                            : terminated ? terminatedAt : admission.Deadline;
                        var reason = completed ? "completed" : terminated ? "terminated" : "deadline";
                        // Rewards first, once per run, for the members present at
                        // the end (IGG: leaving during the mission forfeits them).
                        await WriteTrainingGroundRewardsAsync(
                            admission, members, reason,
                            completed ? completedAt - admission.StartedAt : null,
                            endStartedAt, cancellationToken);
                        var ending = BeginInstanceRunEnd(instanceId,
                            InstanceRunKind.TrainingGround, "TrainingGround",
                            runtime.MapId,
                            checked((ushort)admission.Ground.ClientSceneId),
                            admission.DailyLimit, TrainingGroundScore(instanceId),
                            endStartedAt);
                        if (members.Length != 0)
                        {
                            await PublishInstanceRunEndAsync(
                                ending,
                                [.. members.Select(ToInstanceRunEndMember)],
                                now,
                                settle: null,
                                cancellationToken);
                        }
                        if (!IsInstanceRunEndWindowOpen(ending, now))
                        {
                            await ExitTrainingGroundMembersAsync(
                                runtime, admission, members, reason, endStartedAt,
                                cancellationToken);
                        }
                    }
                    else
                    {
                        await PublishTrainingGroundRunUiAsync(
                            runtime, admission, members, now, cancellationToken);
                    }

                    if (members.Length == 0 &&
                        (admission.Entrants.Count != 0 || completed || terminated || expired))
                    {
                        _trainingRetirements.TryAdd(instanceId, 0);
                    }
                    await TryRetireTrainingGroundRuntimeAsync(
                        instanceId,
                        cancellationToken);
                }
                catch (Exception error) when (
                    error is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
                {
                    LogTrainingGroundDeferred(instanceId, now, error);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _trainingWorldInFlight, 0);
        }
    }

    /// <summary>
    /// One reward row per member present at the end, written once per run (the
    /// (run, character) primary key is the durable guard; the flag only saves a
    /// round trip). A failed write is retried on the next tick while the run's
    /// leave window is open.
    /// </summary>
    private async Task WriteTrainingGroundRewardsAsync(
        TrainingGroundAdmission admission,
        IReadOnlyList<GameSessionContext> members,
        string reason,
        TimeSpan? clearedAfter,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        if (admission.RewardsWritten || members.Count == 0)
        {
            return;
        }
        var store = TrainingGroundStore;
        if (store is null)
        {
            admission.RewardsWritten = true;
            Console.Error.WriteLine(
                $"[training] no reward store; run {admission.ReservationId} pays nothing");
            return;
        }

        var score = TrainingGroundScore(admission.InstanceId);
        var reward = admission.Ground.Reward(score, clearedAfter); // WARRIOR (F-51): this ground's table
        // Heracles keeps its "reason:tier" outcome; the Warriors' grounds add the ground key.
        var tier = admission.Ground.MapId == TrainingGroundPolicy.MapId
            ? reward.Tier
            : $"{admission.Ground.Key}:{reward.Tier}";
        var elapsed = checked((int)Math.Clamp(
            Math.Round((endedAt - admission.StartedAt).TotalSeconds), 0,
            admission.Ground.TimeLimitSeconds));
        var grants = members
            .Select(member => member.CharacterId)
            .Distinct()
            .Select(characterId => new TrainingGroundRewardGrant(
                admission.ReservationId, characterId, admission.RealmId, score,
                $"{reason}:{tier}", elapsed, reward.Experience, reward.TalentPoints))
            .ToArray();
        try
        {
            var inserted = await store.RecordRewardsAsync(grants, cancellationToken);
            admission.RewardsWritten = true;
            Console.WriteLine(
                $"[training] rewards recorded run={admission.ReservationId} reason={reason} " +
                $"score={score} tier={reward.Tier} exp={reward.Experience} tp={reward.TalentPoints} " +
                $"members={grants.Length} new={inserted}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[training] reward write failed run={admission.ReservationId}: {error.Message}");
        }
    }

    private async Task PublishTrainingGroundRunUiAsync(
        WorldInstanceRuntime runtime,
        TrainingGroundAdmission admission,
        IReadOnlyList<GameSessionContext> members,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (members.Count == 0)
        {
            return;
        }

        var remaining = checked((int)Math.Clamp(
            Math.Ceiling((admission.Deadline - now).TotalSeconds),
            0,
            admission.Ground.TimeLimitSeconds));
        var roster = SnapshotInstanceRoster(
            runtime.InstanceId, InstanceRunKind.TrainingGround, now);
        var signature = InstanceRosterSignature(roster);
        var score = TrainingGroundScore(runtime.InstanceId);
        var stamp = new TrainingGroundUiStamp(runtime.InstanceId, remaining, signature, score);

        foreach (var member in members)
        {
            if (!member.WorldReady)
            {
                continue;
            }
            Task? write = null;
            lock (_gate)
            {
                if (!IsCurrentTrainingGroundMember(member, runtime.InstanceId))
                {
                    continue;
                }
                _trainingUi.TryGetValue(member.Session, out var previous);
                if (previous == stamp)
                {
                    continue;
                }

                var packets = new List<ReadOnlyMemory<byte>>();
                if (previous?.InstanceId != stamp.InstanceId)
                {
                    packets.Add(PacketBuilder.RepetitionSync(
                        checked((ushort)admission.Ground.ClientSceneId),
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
                    _trainingUi[member.Session] = stamp;
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

    private async Task ExitTrainingGroundMembersAsync(
        WorldInstanceRuntime runtime,
        TrainingGroundAdmission admission,
        IReadOnlyList<GameSessionContext> members,
        string reason,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        if (members.Count == 0)
        {
            return;
        }

        var transferred = await EgressInstanceRunEndAsync(
            BeginInstanceRunEnd(runtime.InstanceId, InstanceRunKind.TrainingGround,
                "TrainingGround", runtime.MapId,
                checked((ushort)admission.Ground.ClientSceneId),
                admission.DailyLimit, TrainingGroundScore(runtime.InstanceId), endedAt),
            [.. members.Select(ToInstanceRunEndMember)],
            TransitionPartyMemberToAuthoritativeInstanceAsync,
            cancellationToken);
        Console.WriteLine(
            "[training] exit instance=" + runtime.InstanceId +
            $" reason={reason} leader={admission.LeaderId} " +
            $"transferred={transferred}");
    }

    private async Task TryRetireTrainingGroundRuntimeAsync(
        WorldInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        if (!_trainingRetirements.ContainsKey(instanceId))
        {
            return;
        }

        for (var phase = 0; phase < 3; phase++)
        {
            if (!WorldInstances.TryFind(instanceId, out var runtime))
            {
                ForgetTrainingGroundRun(instanceId);
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
                ForgetTrainingGroundRun(instanceId);
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

    private GameSessionContext[] SnapshotTrainingGroundMembersLocked(
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

    private bool IsCurrentTrainingGroundMember(
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

    // Reconnect / roster helpers (one-line hooks in the shared switches).
    private bool IsTrainingGroundReconnectActive(WorldInstanceId instanceId) =>
        _trainingAdmissions.TryGetValue(instanceId, out var admission) &&
        !_trainingTerminations.ContainsKey(instanceId) &&
        !TryGetTrainingGroundCompletion(instanceId, out _) &&
        DateTimeOffset.UtcNow < admission.Deadline;

    private bool IsTrainingGroundReconnectMember(WorldInstanceId instanceId, int characterId) =>
        _trainingAdmissions.TryGetValue(instanceId, out var admission) &&
        admission.Entrants.Contains(characterId);

    private int TrainingGroundRosterLeader(WorldInstanceId instanceId) =>
        _trainingAdmissions.TryGetValue(instanceId, out var admission) ? admission.LeaderId : 0;

    private IEnumerable<WorldInstanceId> TrainingGroundRunIds => _trainingAdmissions.Keys;

    private void ForgetTrainingGroundRun(WorldInstanceId instanceId)
    {
        if (_trainingAdmissions.TryRemove(instanceId, out var admission))
        {
            CloseMemberEntryWindow(admission.LeaderSession);
        }
        ForgetInstanceRunEnd(instanceId);
        _trainingTerminations.TryRemove(instanceId, out _);
        _trainingRetirements.TryRemove(instanceId, out _);
        _trainingLastError.TryRemove(instanceId, out _);
        _trainingProgress.TryRemove(instanceId, out _);
        ForgetInstanceRunMembership(instanceId);
        foreach (var cached in _trainingUi
                     .Where(pair => pair.Value.InstanceId == instanceId))
        {
            _trainingUi.TryRemove(cached);
        }
    }

    private void LogTrainingGroundDeferred(
        WorldInstanceId instanceId,
        DateTimeOffset now,
        Exception error)
    {
        if (_trainingLastError.TryAdd(instanceId, now) ||
            _trainingLastError.TryGetValue(instanceId, out var previous) &&
            now - previous >= TimeSpan.FromSeconds(30) &&
            _trainingLastError.TryUpdate(instanceId, now, previous))
        {
            Console.Error.WriteLine(
                $"[training] deferred instance={instanceId}: {error.Message}");
        }
    }

    private sealed class TrainingGroundAdmission(
        GameSessionRegistry owner,
        WorldInstanceId instanceId,
        Guid reservationId,
        ushort dailyLimit,
        ClientSession leaderSession,
        LegacyInstancePartyMember[] originalMembers,
        DateTimeOffset startedAt,
        short realmId,
        TrainingGroundDefinition ground)
    {
        /// <summary>WARRIOR (F-51): the ground this run is on (map, scene, clock, scoring, rewards).</summary>
        public TrainingGroundDefinition Ground { get; } = ground;
        public WorldInstanceId InstanceId { get; } = instanceId;
        public Guid ReservationId { get; } = reservationId;
        public ushort DailyLimit { get; } = dailyLimit;
        public short RealmId { get; } = realmId;
        public int LeaderId => owner.InstanceRunLeaderCharacterId(instanceId);
        public ClientSession LeaderSession { get; } = leaderSession;
        public LegacyInstancePartyMember[] OriginalMembers { get; } = originalMembers;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public HashSet<int> Entrants { get; } = [];
        public bool RewardsWritten { get; set; }

        public DateTimeOffset Deadline => StartedAt + Ground.TimeLimit;
    }

    private sealed record TrainingGroundUiStamp(
        WorldInstanceId InstanceId,
        int RemainingSeconds,
        string Roster,
        int Score);
}
