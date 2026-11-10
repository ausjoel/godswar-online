using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const ushort MedusaRepetitionIndex = 0;
    private const ushort MedusaRepetitionGroupIndex = 0;
    private const ushort MedusaRepetitionActiveState = 5;
    private static readonly TimeSpan MedusaCompletionExitDelay =
        TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<
        WorldInstanceId,
        MedusaLeaderUiRegistration> _medusaLeaderUi = [];
    // Admitted members get the same progress panel as the leader. The panel
    // content is instance-scoped (score, remaining time, roster), so a member
    // reads the leader registration's scene and daily limit and carries only its
    // own per-session synchronization state. Termination authority stays with
    // _medusaLeaderUi; nothing here can end a run.
    private readonly ConcurrentDictionary<
        (WorldInstanceId Instance, ClientSession Session),
        MedusaLeaderUiRegistration> _medusaMemberUi = [];

    internal bool TryRegisterMedusaLeaderUi(
        WorldInstanceId worldInstanceId,
        ClientSession session,
        int leaderCharacterId,
        ushort dailyEntryLimit,
        IReadOnlyList<InstanceRosterEntry>? roster = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!worldInstanceId.IsValid ||
            leaderCharacterId <= 0 ||
            dailyEntryLimit == 0 ||
            !_sessions.TryGetValue(session, out var context) ||
            context.CharacterId != leaderCharacterId ||
            context.WorldInstanceId != worldInstanceId ||
            !WorldInstances.TryFind(worldInstanceId, out var runtime))
        {
            return false;
        }

        var ownership = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                out var snapshot)
                    ? snapshot
                    : null);
        if (ownership is null ||
            ownership.Run.State != MedusaRunState.Active ||
            !ownership.Run.AdmittedCharacterIds.Contains(
                leaderCharacterId) ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                ownership.ContentMapId.Value,
                out var clientSceneId) ||
            clientSceneId <= 0)
        {
            return false;
        }

        var registered = _medusaLeaderUi.TryAdd(
            worldInstanceId,
            new(
                this,
                worldInstanceId,
                session,
                leaderCharacterId,
                checked((ushort)clientSceneId),
                dailyEntryLimit,
                roster ?? []));
        if (registered)
        {
            // The run's own leader identity, in the one per-run leader store
            // every dungeon shares. It is what the end control reads, so the
            // leader keeps the control after a relog.
            BeginInstanceRunLeader(worldInstanceId, leaderCharacterId);
            // The one per-run member record the roster publishes from opens here,
            // with the party this run was registered for.
            BeginInstanceRunMembership(
                worldInstanceId,
                roster ?? []);
        }

        return registered;
    }

    /// <summary>
    /// Opens the running instance's progress panel for one admitted member.
    /// </summary>
    /// <remarks>
    /// The member sees the instance's own score, remaining time and roster - the
    /// same values the leader sees - so a member who joins a run that is already
    /// in progress reads the current progress instead of a fresh run's zeroes.
    /// Leadership, and with it the end-instance control, is unaffected.
    /// </remarks>
    internal bool TryRegisterMedusaMemberUi(
        ClientSession session,
        WorldInstanceId worldInstanceId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!worldInstanceId.IsValid ||
            !_sessions.TryGetValue(session, out var context) ||
            context.WorldInstanceId != worldInstanceId ||
            !_medusaLeaderUi.TryGetValue(worldInstanceId, out var leaderUi) ||
            !WorldInstances.TryFind(worldInstanceId, out var runtime))
        {
            return false;
        }

        var ownership = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                out var snapshot)
                    ? snapshot
                    : null);
        if (ownership is null ||
            ownership.Run.State != MedusaRunState.Active ||
            !ownership.Run.AdmittedCharacterIds.Contains(context.CharacterId))
        {
            return false;
        }

        return _medusaMemberUi.TryAdd(
            (worldInstanceId, session),
            new(
                this,
                worldInstanceId,
                session,
                context.CharacterId,
                leaderUi.ClientSceneId,
                leaderUi.DailyEntryLimit));
    }
    /// <summary>
    /// Reopens a reconnecting member's Medusa progress panel.
    /// </summary>
    /// <remarks>
    /// Medusa publishes its panel from per-session registrations rather than
    /// from the run on every tick, so a login that puts a member back inside the
    /// instance has to reopen his registration or the panel stays empty. A
    /// returning leader whose own registration is still held by his dropped
    /// session takes that registration back, so the panel and the native
    /// end-instance control follow him exactly as they did on entry. If the run's
    /// leader registration is already gone the run has no leader authority to
    /// restore, and the member registration is opened instead.
    /// </remarks>
    internal void RestoreMedusaInstancePanelAfterReconnect(
        ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryGetValue(session, out var context) ||
            context.Session.IsDisconnected)
        {
            return;
        }

        var worldInstanceId = context.WorldInstanceId;
        if (_medusaLeaderUi.TryGetValue(worldInstanceId,
                out var registration) &&
            registration.CharacterId == context.CharacterId &&
            (ReferenceEquals(registration.Session, session) ||
             registration.Session.IsDisconnected))
        {
            if (!ReferenceEquals(registration.Session, session))
            {
                _medusaLeaderUi[worldInstanceId] =
                    new MedusaLeaderUiRegistration(
                        this,
                        worldInstanceId,
                        session,
                        registration.CharacterId,
                        registration.ClientSceneId,
                        registration.DailyEntryLimit,
                        registration.Roster);
                Console.WriteLine(
                    "[medusa] reconnected leader panel instance=" +
                    $"{worldInstanceId} " +
                    $"character={context.CharacterId}");
            }
            return;
        }

        if (TryRegisterMedusaMemberUi(session, worldInstanceId))
        {
            Console.WriteLine(
                "[medusa] reconnected member panel instance=" +
                $"{worldInstanceId} character={context.CharacterId}");
        }
    }

    internal bool TryEndMedusaRunFromLeader(
        ClientSession session,
        int repetitionId,
        int repetitionIndex,
        DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Only the instance's own leader may end it, and the leader is the run's
        // own mutable character id - never the session object that happened to
        // register the panel, so a leader who drops and relogs keeps the control.
        // Party leadership is an unrelated system: ANDing it here locked the
        // control out whenever the instance leader was not the party leader.
        if (repetitionIndex != MedusaRepetitionIndex ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady || context.Session.IsDisconnected ||
            !context.Ownership.IsValid ||
            !IsCurrentAccountSession(context.AccountId, context.Session,
                context.Ownership) ||
            context.CharacterId !=
                InstanceRunLeaderCharacterId(context.WorldInstanceId) ||
            !WorldInstances.TryFind(
                context.WorldInstanceId,
                out var runtime))
        {
            return false;
        }

        var ownership = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                    out var snapshot)
                ? snapshot
                : null);
        if (ownership is null ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                ownership.ContentMapId.Value, out var clientSceneId) ||
            repetitionId != clientSceneId)
        {
            return false;
        }
        // A terminal run is the shared end-of-run flow's: this same native
        // control is then the member's own leave, which the dispatcher resolves
        // before it reaches here, and it takes only the member who pressed it.
        if (ownership.Run.State != MedusaRunState.Active)
        {
            return false;
        }

        var result = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                var routed = map.TryAbandonMedusaRun(
                    context.CharacterId,
                    requestedAt,
                    out var abandoned);
                return (routed, abandoned);
            });
        if (!result.routed ||
            result.abandoned.RunOutcome is not (
                MedusaRunAbandonOutcome.Exited or
                MedusaRunAbandonOutcome.TimedOut or
                MedusaRunAbandonOutcome.RunNotActive))
        {
            return false;
        }

        RequestMedusaTerminationExit(context.WorldInstanceId);
        // The leader registration stays in place: it is what publishes the
        // running panel, and the shared end-of-run flow publishes the terminal
        // one to every member inside.
        return true;
    }

    internal bool TryTerminateMedusaRunFromLeader(
        ClientSession session,
        DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryGetValue(session, out var context) ||
            context.CharacterId !=
                InstanceRunLeaderCharacterId(context.WorldInstanceId) ||
            !WorldInstances.TryFind(context.WorldInstanceId, out var runtime))
        {
            return false;
        }

        short? contentMapId = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(out var snapshot)
                ? (short)snapshot.ContentMapId.Value
                : (short?)null);
        if (contentMapId is not { } mapId ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(mapId,
                out var clientSceneId))
        {
            return false;
        }

        return TryEndMedusaRunFromLeader(
            session,
            clientSceneId,
            MedusaRepetitionIndex,
            requestedAt);
    }

    private MedusaLeaderUiDelivery? CaptureMedusaLeaderUiDelivery(
        WorldInstanceRuntime runtime,
        DateTimeOffset now)
    {
        if (!_medusaLeaderUi.TryGetValue(
                runtime.InstanceId,
                out var registration))
        {
            return null;
        }

        var state = InvokeWorldOwner(
            runtime,
            map =>
            {
                var ownership = map.TryGetMedusaOwnershipSnapshot(
                    out var snapshot)
                        ? snapshot
                        : null;
                var leader = map.Snapshot().SingleOrDefault(context =>
                    ReferenceEquals(
                        context.Session,
                        registration.Session) &&
                    context.CharacterId == registration.CharacterId);
                return (ownership, leader);
            });
        if (state.ownership is null ||
            state.leader is null ||
            registration.Session.IsDisconnected)
        {
            _medusaLeaderUi.TryRemove(
                new KeyValuePair<
                    WorldInstanceId,
                    MedusaLeaderUiRegistration>(
                    runtime.InstanceId,
                    registration));
            return null;
        }
        if (!state.leader.WorldReady)
        {
            return null;
        }

        var run = state.ownership.Run;
        // A terminal run's panel is the shared end-of-run flow's to publish: the
        // four native frames and the two countdowns every dungeon shows. The
        // leader registration stays in place - it is what publishes the running
        // panel - and the terminal run is published from the tick's own capture.
        if (run.State != MedusaRunState.Active)
        {
            return null;
        }
        var remainingSeconds = checked((int)Math.Clamp(
            Math.Ceiling((run.Deadline - now).TotalSeconds),
            0d,
            int.MaxValue));
        var packets = registration.CaptureUpdate(
            remainingSeconds,
            run.TeamScore);
        return packets.Count == 0
            ? null
            : new(
                runtime.InstanceId,
                registration,
                packets,
                RemoveAfterSend: false);
    }

    /// <summary>
    /// The progress-panel deliveries owed to admitted members of a running
    /// instance, using the instance's own score, remaining time and roster.
    /// </summary>
    private List<MedusaLeaderUiDelivery> CaptureMedusaMemberUiDeliveries(
        WorldInstanceRuntime runtime,
        DateTimeOffset now)
    {
        var deliveries = new List<MedusaLeaderUiDelivery>();
        if (_medusaMemberUi.IsEmpty)
        {
            return deliveries;
        }

        var state = InvokeWorldOwner(
            runtime,
            static map => (
                Ownership: map.TryGetMedusaOwnershipSnapshot(
                    out var snapshot)
                        ? snapshot
                        : null,
                Players: map.Snapshot()));
        if (state.Ownership is null)
        {
            RemoveMedusaMemberUi(runtime.InstanceId);
            return deliveries;
        }

        var run = state.Ownership.Run;
        foreach (var entry in _medusaMemberUi)
        {
            if (entry.Key.Instance != runtime.InstanceId)
            {
                continue;
            }
            var registration = entry.Value;
            var member = state.Players.FirstOrDefault(context =>
                ReferenceEquals(
                    context.Session,
                    registration.Session) &&
                context.CharacterId == registration.CharacterId);
            if (member is null || registration.Session.IsDisconnected)
            {
                _medusaMemberUi.TryRemove(entry);
                continue;
            }
            if (!member.WorldReady)
            {
                continue;
            }

            // A terminal run's panel is the shared end-of-run flow's to publish,
            // for every member exactly as for the leader.
            if (run.State != MedusaRunState.Active)
            {
                continue;
            }

            var remainingSeconds = checked((int)Math.Clamp(
                Math.Ceiling((run.Deadline - now).TotalSeconds),
                0d,
                int.MaxValue));
            var packets = registration.CaptureUpdate(
                remainingSeconds,
                run.TeamScore);
            if (packets.Count != 0)
            {
                deliveries.Add(new(
                    runtime.InstanceId,
                    registration,
                    packets,
                    RemoveAfterSend: false));
            }
        }

        return deliveries;
    }

    private void RemoveMedusaMemberUi(WorldInstanceId instanceId)
    {
        foreach (var entry in _medusaMemberUi)
        {
            if (entry.Key.Instance == instanceId)
            {
                _medusaMemberUi.TryRemove(entry);
            }
        }
    }

    private async Task PublishMedusaLeaderUiDeliveryAsync(
        MedusaLeaderUiDelivery delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var packet in delivery.Packets)
            {
                await delivery.Registration.Session.SendAsync(
                    packet,
                    cancellationToken,
                    "MedusaLeaderInstancePanel");
            }
        }
        catch (Exception error) when (
            error is IOException or ObjectDisposedException)
        {
            Remove(delivery.Registration.Session);
            _medusaMemberUi.TryRemove(
                (delivery.WorldInstanceId, delivery.Registration.Session),
                out _);
        }
        finally
        {
            if (delivery.RemoveAfterSend &&
                !_medusaLeaderUi.TryRemove(
                    new KeyValuePair<
                        WorldInstanceId,
                        MedusaLeaderUiRegistration>(
                        delivery.WorldInstanceId,
                        delivery.Registration)))
            {
                _medusaMemberUi.TryRemove(
                    (delivery.WorldInstanceId, delivery.Registration.Session),
                    out _);
            }
            else if (delivery.RemoveAfterSend)
            {
                ForgetInstanceRunMembership(delivery.WorldInstanceId);
            }
        }
    }

    private sealed class MedusaLeaderUiRegistration(
        GameSessionRegistry owner,
        WorldInstanceId instanceId,
        ClientSession session,
        int characterId,
        ushort clientSceneId,
        ushort dailyEntryLimit,
        IReadOnlyList<InstanceRosterEntry>? roster = null)
    {
        private readonly object _gate = new();
        private bool _synchronized;
        private int _lastRemainingSeconds = -1;
        private int _lastTeamScore = -1;

        public ClientSession Session { get; } = session;

        /// <summary>
        /// The character this registration publishes the panel for: the leader
        /// for the run's own registration, and the member for a member's.
        /// </summary>
        public int CharacterId { get; } = characterId;

        /// <summary>
        /// The run's one leader identity, read from the shared per-run leader
        /// store. The registration is not the leadership - it is only the object
        /// that publishes a panel - so a leader who drops and relogs keeps the
        /// run's own end control, and the panel republishes under the run's
        /// current leader.
        /// </summary>
        public int LeaderCharacterId => owner.InstanceRunLeaderCharacterId(instanceId);

        public ushort ClientSceneId { get; } = clientSceneId;

        public ushort DailyEntryLimit { get; } = dailyEntryLimit;

        /// <summary>
        /// The party the run was admitted for. It is the run's own registration
        /// record (the login reconnect reads it); the published member roster
        /// comes from the one per-run membership record instead.
        /// </summary>
        public IReadOnlyList<InstanceRosterEntry> Roster { get; } =
            roster ?? [];

        public IReadOnlyList<byte[]> CaptureUpdate(
            int remainingSeconds,
            int teamScore)
        {
            lock (_gate)
            {
                if (_synchronized &&
                    _lastRemainingSeconds == remainingSeconds &&
                    _lastTeamScore == teamScore)
                {
                    return [];
                }

                _lastRemainingSeconds = remainingSeconds;
                _lastTeamScore = teamScore;
                var fight = PacketBuilder.RepetitionFightInfo(
                    remainingSeconds,
                    teamScore);
                if (_synchronized)
                {
                    return [fight];
                }

                _synchronized = true;
                return
                [
                    PacketBuilder.RepetitionSync(
                        ClientSceneId,
                        MedusaRepetitionIndex,
                        MedusaRepetitionGroupIndex,
                        MedusaRepetitionActiveState,
                        DailyEntryLimit),
                    fight
                ];
            }
        }

    }

    private sealed record MedusaLeaderUiDelivery(
        WorldInstanceId WorldInstanceId,
        MedusaLeaderUiRegistration Registration,
        IReadOnlyList<byte[]> Packets,
        bool RemoveAfterSend);
}

