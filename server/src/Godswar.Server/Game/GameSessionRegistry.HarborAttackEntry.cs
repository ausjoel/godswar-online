using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// The same native sixty-second Enter window the leader receives, published to
/// the rest of the admitted party.
/// </summary>
/// <remarks>
/// This carries no clock and no daily-entry accounting of its own. It records
/// which members confirmed, and the leader's own admission then transfers exactly
/// those members; every other member is released by the existing
/// <c>ReleaseLegacyInstanceDailyEntryMembersAsync</c> path, which is already the
/// rule "an attempt is only spent by a character who actually entered".
/// </remarks>
internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<ClientSession, MemberEntryWindow>
        _memberEntryWindows = [];

    /// <summary>
    /// How long a published Enter window stays live. It is the client's own
    /// countdown - the same sixty seconds the leader's window gets - and with the
    /// window it is what the member roster's 「等待中」 state expires on.
    /// </summary>
    private static readonly TimeSpan MemberEntryWindowLifetime =
        GameClientHandler.InstanceEntryCountdownDuration;

    /// <summary>
    /// Gives every other admitted member the leader's own Enter window.
    /// </summary>
    internal async Task PublishMemberEntryWindowsAsync(
        ClientSession leaderSession,
        LegacyInstancePartySnapshot party,
        int clientSceneId,
        InstanceCallerEntryKind kind,
        CancellationToken cancellationToken)
    {
        if (party.Members.Count < 2)
        {
            return;
        }

        MemberEntryWindow window;
        List<ClientSession> windows;
        lock (_gate)
        {
            if (_memberEntryWindows.TryGetValue(
                    leaderSession,
                    out var previous))
            {
                CloseMemberEntryWindowLocked(previous);
            }
            window = new(leaderSession, clientSceneId, kind,
                party.Members.ToArray(), DateTimeOffset.UtcNow +
                MemberEntryWindowLifetime);
            _memberEntryWindows[leaderSession] = window;
            foreach (var member in party.Members)
            {
                if (ReferenceEquals(member.Session, leaderSession))
                {
                    continue;
                }
                _memberEntryWindows[member.Session] = window;
                window.Sessions.Add(member.Session);
            }
            windows = [.. window.Sessions];
        }

        // Written outside the registry gate: an awaiting transport write must
        // never hold it, and the window is already visible to its owner.
        foreach (var session in windows)
        {
            try
            {
                await session.SendAsync(
                    PacketBuilder.InstanceEntryQueueState(clientSceneId),
                    cancellationToken,
                    "HarborAttackEntryQueued");
                await session.SendAsync(
                    PacketBuilder.InstanceEntryNotice(clientSceneId),
                    cancellationToken,
                    "HarborAttackEntryCountdown");
            }
            catch (Exception error) when (
                error is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine(
                    "[harbor] entry window failed: " + error.Message);
            }
        }

        Console.WriteLine(
            "[harbor] entry windows party=" + party.Members.Count +
            $" scene={clientSceneId} windows={window.Sessions.Count}");
    }

    /// <summary>
    /// Whether this frame is a member's answer to their own Enter window.
    /// </summary>
    internal bool IsMemberEntryWindowMember(
        ClientSession session,
        int clientSceneId)
    {
        lock (_gate)
        {
            return _memberEntryWindows.TryGetValue(
                    session,
                    out var window) &&
                !window.Closed &&
                window.ClientSceneId == clientSceneId &&
                !ReferenceEquals(window.LeaderSession, session);
        }
    }

    /// <summary>
    /// The instance a session's own open window points at, for the one question
    /// that has to be asked before the gate: whether that character is already on
    /// the way in.
    /// </summary>
    private WorldInstanceId ResolveMemberEntryWindowTarget(
        ClientSession session,
        int clientSceneId)
    {
        lock (_gate)
        {
            return _memberEntryWindows.TryGetValue(
                    session,
                    out var window) &&
                !window.Closed &&
                window.ClientSceneId == clientSceneId &&
                !ReferenceEquals(window.LeaderSession, session)
                ? window.TargetInstanceId ?? default
                : default;
        }
    }

    /// <summary>
    /// Records a member's confirmation, and reports the running instance they
    /// must join when the leader has already committed one.
    /// </summary>
    /// <remarks>
    /// A member's client can resend the confirmation while the first one is still
    /// being admitted. The first confirmation already minted the reservation he
    /// enters on, so a repeat is acknowledged and otherwise ignored: without this
    /// the same character was queued once per frame, each queue entry carrying its
    /// own daily-entry reservation.
    /// </remarks>
    internal bool TryConfirmMemberEntryWindow(
        ClientSession session,
        int clientSceneId,
        out MemberEntryJoin join)
    {
        join = default;
        // The admission answers this before the gate is taken: the in-flight
        // record is claimed under the same gate, so asking from here cannot
        // deadlock with it.
        if (IsMemberEntryJoinInFlight(session, ResolveMemberEntryWindowTarget(
                session,
                clientSceneId)))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_memberEntryWindows.TryGetValue(
                    session,
                    out var window) ||
                window.Closed ||
                window.ClientSceneId != clientSceneId ||
                ReferenceEquals(window.LeaderSession, session) ||
                !_sessions.TryGetValue(session, out var context))
            {
                return false;
            }
            var member = window.Members.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Session, session));
            if (member is null)
            {
                return false;
            }
            window.Confirmed.Add(member.CharacterId);
            if (window.TargetInstanceId is not { } target ||
                window.TargetMapId == 0 ||
                context.WorldInstanceId == target)
            {
                // The leader's own window is still open: the member travels with
                // the leader's transfer instead.
                return false;
            }

            join = new(
                window.Kind,
                target,
                window.TargetMapId,
                window.TargetArrivalX,
                window.TargetArrivalZ,
                context.WorldInstanceId,
                context.MapId,
                context.Ownership,
                context.RealmId);
            return true;
        }
    }

    /// <summary>
    /// Records a member's answer of "no" to their own Enter window. The window
    /// itself stays open for the rest of the party, but that member is no longer
    /// waiting on it and leaves the published member roster.
    /// </summary>
    internal void RecordMemberEntryDeclined(ClientSession session)
    {
        lock (_gate)
        {
            if (_memberEntryWindows.TryGetValue(
                    session,
                    out var window) &&
                !window.Closed)
            {
                var member = window.Members.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.Session, session));
                if (member is not null)
                {
                    window.Declined.Add(member.CharacterId);
                }
            }
        }
    }

    /// <summary>
    /// Binds the committed run so a member who confirms afterwards joins it.
    /// </summary>
    internal void BindMemberEntryRun(
        ClientSession leaderSession,
        WorldInstanceId instanceId,
        byte mapId,
        float arrivalX,
        float arrivalZ)
    {
        lock (_gate)
        {
            if (_memberEntryWindows.TryGetValue(
                    leaderSession,
                    out var window) &&
                !window.Closed)
            {
                window.TargetInstanceId = instanceId;
                window.TargetMapId = mapId;
                window.TargetArrivalX = arrivalX;
                window.TargetArrivalZ = arrivalZ;
            }
        }
    }

    /// <summary>
    /// Whether the leader's own entry already committed a run this session's
    /// remaining windows can still join.
    /// </summary>
    internal bool IsMemberEntryRunBound(ClientSession leaderSession)
    {
        lock (_gate)
        {
            return _memberEntryWindows.TryGetValue(
                    leaderSession,
                    out var window) &&
                !window.Closed &&
                window.TargetInstanceId is not null;
        }
    }

    /// <summary>
    /// The members who confirmed, for the leader's own transfer.
    /// </summary>
    internal IReadOnlySet<int> ConfirmedMemberEntryMembers(
        ClientSession leaderSession)
    {
        lock (_gate)
        {
            return _memberEntryWindows.TryGetValue(
                    leaderSession,
                    out var window)
                ? new HashSet<int>(window.Confirmed)
                : new HashSet<int>();
        }
    }

    /// <summary>
    /// Ends the party's windows once the leader's own entry resolved, closing the
    /// members' native windows with the same reset the leader's window uses.
    /// </summary>
    internal async Task CloseMemberEntryWindowsAsync(
        ClientSession leaderSession)
    {
        MemberEntryWindow? window;
        List<ClientSession> windows = [];
        lock (_gate)
        {
            if (!_memberEntryWindows.TryGetValue(
                    leaderSession,
                    out window))
            {
                return;
            }
            windows = [.. window.Sessions];
            CloseMemberEntryWindowLocked(window);
        }

        foreach (var session in windows)
        {
            try
            {
                await session.SendAsync(
                    PacketBuilder.RepetitionReset(),
                    CancellationToken.None,
                    "HarborAttackEntryClosed");
            }
            catch (Exception error)
            {
                Console.WriteLine(
                    "[harbor] entry window close failed: " + error.Message);
            }
        }
    }

    /// <summary>
    /// The windows still open for a session, so a disconnect can drop them.
    /// </summary>
    internal void CloseMemberEntryWindow(ClientSession leaderSession)
    {
        lock (_gate)
        {
            if (_memberEntryWindows.TryGetValue(
                    leaderSession,
                    out var window))
            {
                CloseMemberEntryWindowLocked(window);
            }
        }
    }

    private void CloseMemberEntryWindowLocked(
        MemberEntryWindow window)
    {
        window.Closed = true;
        foreach (var session in window.Sessions.Append(window.LeaderSession))
        {
            _memberEntryWindows.TryRemove(session, out _);
        }
    }

    /// <summary>
    /// Drops one session's own Enter window once that session's entry is over -
    /// admitted, released or refused. A member admitted into someone else's run
    /// kept his window record otherwise, and his next entry into a run of his own
    /// was then matched against that stale record and misread as a member
    /// confirmation: the click queued a member join that could never transfer,
    /// while his real entry waited for his own window to count down.
    /// </summary>
    internal void ForgetMemberEntryWindow(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _memberEntryWindows.TryRemove(session, out _);
    }

    private sealed class MemberEntryWindow(
        ClientSession leaderSession,
        int clientSceneId,
        InstanceCallerEntryKind kind,
        LegacyInstancePartyMember[] members,
        DateTimeOffset expiresAt)
    {
        public ClientSession LeaderSession { get; } = leaderSession;
        public int ClientSceneId { get; } = clientSceneId;
        public InstanceCallerEntryKind Kind { get; } = kind;
        public LegacyInstancePartyMember[] Members { get; } = members;
        public List<ClientSession> Sessions { get; } = [];
        public HashSet<int> Confirmed { get; } = [];
        // Members who answered "no": still in the party, no longer waiting on this
        // window, so the member roster leaves them out.
        public HashSet<int> Declined { get; } = [];
        // The window the client is counting down. Once it is past, a member who
        // never confirmed is no longer waiting and leaves the roster; the entry
        // rules themselves are unchanged.
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool Closed { get; set; }
        public WorldInstanceId? TargetInstanceId { get; set; }
        public byte TargetMapId { get; set; }
        public float TargetArrivalX { get; set; }
        public float TargetArrivalZ { get; set; }
    }
}

/// <summary>
/// The committed run a confirming member joins directly, whatever the instance.
/// </summary>
internal readonly record struct MemberEntryJoin(
    InstanceCallerEntryKind Kind,
    WorldInstanceId TargetInstanceId,
    byte TargetMapId,
    float ArrivalX,
    float ArrivalZ,
    WorldInstanceId SourceWorldInstanceId,
    byte SourceMapId,
    PlayerOwnershipFence Ownership,
    RealmId RealmId);
