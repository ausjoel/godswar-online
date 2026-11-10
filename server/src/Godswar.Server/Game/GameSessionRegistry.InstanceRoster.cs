using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// The member list every instance progress panel publishes (opcode 10218), the
/// one per-run member record it is built from, and the one place in the server
/// that decides which of the client's four states each member is in.
/// </summary>
/// <remarks>
/// 飘渺幻境, 亚特兰蒂斯, 港湾遇袭 and 美杜莎之岛 all publish this same
/// implementation, and all of them keep their members in the same
/// <see cref="InstanceRunMembership"/> record: which members the run was
/// registered for, and which of them actually came in. Nothing selects a member
/// source by run kind any more - the run kind only names the record that owns the
/// run's leader identity, which every run transfers on its own.
/// <para>
/// The record is written in exactly two places, both on the shared admission
/// path: <see cref="BeginInstanceRunMembership"/> when a run is registered for a
/// party, and <see cref="RecordInstanceRunMemberEntry"/> when a member is
/// actually pulled into the run (including an invited member who was never on
/// the registered party). The snapshot below also records every member it sees
/// inside, which is the run's own evidence that he came in.
/// </para>
/// <para>
/// The list is the run's own roster and never the set of sessions that happens to
/// be connected at the moment the panel is built. A member who drops stays on the
/// list as offline; a member who never came in waits while his invitation or
/// Enter window is live, keeps waiting while his admitted entry is on its way in,
/// and leaves the list once neither is true. A row with a live session is never
/// dropped, and a run whose records cannot be read keeps its members rather than
/// treating a timeout as "he never came in".
/// </para>
/// <para>
/// Nothing here awaits, and no world owner is invoked: the gate is reentrant, so
/// a caller that already holds it is unaffected, and physical writes still happen
/// after it, in the publishing callers.
/// </para>
/// </remarks>
internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// The one member record per run instance the roster is published from.
    /// Touched under the registry gate only.
    /// </summary>
    private readonly Dictionary<WorldInstanceId, InstanceRunMembership>
        _instanceRunMemberships = [];

    /// <summary>
    /// Which run a roster belongs to. The value selects the record that owns the
    /// run's leader identity, and nothing else: members, entrants and waiting are
    /// read from the run's one membership record.
    /// </summary>
    /// <remarks>
    /// Kept separate from <c>InstanceCallerEntryKind</c>, which describes entry
    /// windows and has no Medusa member.
    /// </remarks>
    internal enum InstanceRunKind : byte
    {
        Wonderland = 1,
        Atlantis = 2,
        HarborAttack = 3,
        Medusa = 4,
        TrainingGround = 5 // TRAINING
    }

    /// <summary>
    /// One recorded member of a run: the values the panel publishes for a member
    /// whose session is no longer connected, with the account and ownership fence
    /// the admission recorded.
    /// </summary>
    internal readonly record struct InstanceRosterEntry(
        int CharacterId,
        string Name,
        int Level,
        byte Profession,
        int AccountId = 0,
        PlayerOwnershipFence Ownership = default);

    /// <summary>
    /// The published member list of one run at one instant: every member with the
    /// single state the client renders.
    /// </summary>
    /// <remarks>
    /// The four states are, in the operator's terms: a live session inside the
    /// instance is 「在线」, and 「在线(队长)」 when it is the run's own leader;
    /// a member who came in earlier but is not inside now is 「离线」; a member
    /// who has not come in yet - invited, registered, or with an admitted entry
    /// still on its way in - is 「等待中」; and a member who never came in and
    /// holds no live invitation is not on the list at all.
    /// </remarks>
    private RepetitionInstanceMember[] SnapshotInstanceRoster(
        WorldInstanceId instanceId,
        InstanceRunKind kind,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            // The one per-run member record: written where a run is registered
            // and where a member is pulled in, read here and nowhere else.
            var membership = InstanceRunMembershipLocked(instanceId);
            var recorded = membership.Members;
            var waiting = InstanceRunWaitingMembers(instanceId, now, recorded);
            var onTheWayIn = PendingInstanceEntryJoins(instanceId);
            var live = LiveInstanceMembers(instanceId);
            var liveIds = new HashSet<int>(live.Length);
            foreach (var context in live)
            {
                liveIds.Add(context.CharacterId);
            }
            // Seeing a member inside is the run's own evidence that he came in, so
            // the row stays on the list as offline once he drops.
            foreach (var context in live)
            {
                membership.RecordEntrant(context.CharacterId);
            }
            var entrants = membership.Entrants;
            var leaderId = InstanceRosterLeader(instanceId, kind);
            // A session inside the run is 「在线」; whether it is the run's leader
            // only decides the state the panel records, because 「在线」 and
            // 「在线(队长)」 share the one byte the client draws (see
            // RepetitionMemberState).
            var onlineStates = new Dictionary<int, RepetitionMemberState>(
                live.Length);
            foreach (var context in live)
            {
                onlineStates[context.CharacterId] =
                    context.CharacterId == leaderId
                        ? RepetitionMemberState.OnlineLeader
                        : RepetitionMemberState.Online;
            }
            var rows = new Dictionary<int, RepetitionInstanceMember>(
                recorded.Count + waiting.Count + live.Length + 5);
            // The recorded roster first, every row offline until a live session
            // is found for it. A recorded level of zero would make the packet
            // builder reject the whole frame, so such a row is shown at level one
            // instead of the member being dropped.
            foreach (var entry in recorded)
            {
                if (entry.CharacterId <= 0)
                {
                    continue;
                }

                var isLive = liveIds.Contains(entry.CharacterId);
                // The run's own record of who came in: recorded the moment his
                // entry was admitted, and by this snapshot whenever he is seen
                // inside. It never depends on a list that only covers the party
                // the run was registered for.
                var cameIn = entrants.Contains(entry.CharacterId);
                // An entry the run has already accepted and whose transfer the
                // tick has not performed yet: the member is on his way in, so he
                // keeps waiting instead of leaving the list between the answer
                // and the arrival.
                var isOnTheWayIn = onTheWayIn.Contains(entry.CharacterId);
                // A member the run knows about keeps his row. Nothing about the
                // run's own bookkeeping may take a teammate off the list: a row
                // the player cannot see is indistinguishable from a member who
                // never joined, and the operator's screen showed exactly that -
                // every teammate left the list the moment he came in. The state
                // byte alone says what he is now: live, away, or still waiting.
                rows[entry.CharacterId] = new(entry.CharacterId, entry.Name,
                    Math.Max(entry.Level, 1),
                    isLive
                        ? onlineStates[entry.CharacterId]
                        : isOnTheWayIn
                            ? RepetitionMemberState.Waiting
                            : cameIn
                                ? RepetitionMemberState.Offline
                                : RepetitionMemberState.Waiting,
                    entry.Profession);
            }

            // An invitation can name a character the run's own record only picks
            // up once he is inside, so the invitation itself carries the row.
            foreach (var entry in waiting.Values)
            {
                if (entry.CharacterId <= 0 ||
                    rows.ContainsKey(entry.CharacterId))
                {
                    continue;
                }

                rows[entry.CharacterId] = new(entry.CharacterId, entry.Name,
                    Math.Max(entry.Level, 1), RepetitionMemberState.Waiting,
                    entry.Profession);
            }

            // A session inside the instance is the member's current state, so it
            // replaces the recorded row with live values and marks it online. It
            // also carries a member the run's record has not caught up with yet.
            foreach (var context in live)
            {
                rows[context.CharacterId] = new(context.CharacterId,
                    context.Character.Name, Math.Max(context.Character.Level, 1),
                    onlineStates[context.CharacterId],
                    context.Character.Profession);
            }

            return [.. rows.Values.OrderBy(static row => row.CharacterId)];
        }
    }

    /// <summary>
    /// Opens a run's one member record for the party it was registered for.
    /// </summary>
    /// <remarks>
    /// Called from the shared registration path of every run (飘渺幻境,
    /// 亚特兰蒂斯, 港湾遇袭 and 美杜莎之岛 all pass their own registered party),
    /// so the record never depends on a per-kind member source.
    /// </remarks>
    internal void BeginInstanceRunMembership(
        WorldInstanceId instanceId,
        IReadOnlyList<InstanceRosterEntry> members)
    {
        if (!instanceId.IsValid)
        {
            return;
        }

        lock (_gate)
        {
            var membership = InstanceRunMembershipLocked(instanceId);
            foreach (var member in members)
            {
                membership.RecordMember(member);
            }
        }
    }

    /// <summary>
    /// Records a member the moment a run pulls him in, as a member and as an
    /// entrant.
    /// </summary>
    /// <remarks>
    /// This is the one write every admission path uses - the shared Enter-window
    /// join of 飘渺幻境, 亚特兰蒂斯 and 港湾遇袭, and 美杜莎之岛's own invitation
    /// admission. An invited member holds his own daily-entry reservation and was
    /// never on the registered party, so this is what puts him on the run's
    /// roster; the row then stays, as online while he is inside and as offline
    /// after he drops.
    /// </remarks>
    internal bool RecordInstanceRunMemberEntry(
        WorldInstanceId instanceId,
        InstanceRosterEntry member)
    {
        if (!instanceId.IsValid || member.CharacterId <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            return InstanceRunMembershipLocked(instanceId)
                .RecordEntrant(member);
        }
    }

    /// <summary>
    /// The run's one member record, created on first use and only ever touched
    /// under the registry gate.
    /// </summary>
    private InstanceRunMembership InstanceRunMembershipLocked(
        WorldInstanceId instanceId)
    {
        if (!_instanceRunMemberships.TryGetValue(instanceId, out var membership))
        {
            membership = new InstanceRunMembership();
            _instanceRunMemberships[instanceId] = membership;
        }

        return membership;
    }

    /// <summary>
    /// Drops a finished run's member record with the run's own records.
    /// </summary>
    private void ForgetInstanceRunMembership(WorldInstanceId instanceId) =>
        _instanceRunMemberships.Remove(instanceId);

    private static InstanceRosterEntry ToRosterEntry(
        LegacyInstancePartyMember member) =>
        new(member.CharacterId, member.CharacterName, member.Level,
            member.Profession, member.AccountId, member.Ownership);

    /// <summary>
    /// The members whose entry a run has already accepted and whose transfer the
    /// world tick has not performed yet.
    /// </summary>
    private HashSet<int> PendingInstanceEntryJoins(WorldInstanceId instanceId)
    {
        var pending = new HashSet<int>();
        foreach (var join in _pendingMemberEntryJoins)
        {
            if (join.Join.TargetInstanceId == instanceId &&
                _sessions.TryGetValue(join.Session, out var context) &&
                context.CharacterId > 0)
            {
                pending.Add(context.CharacterId);
            }
        }

        return pending;
    }

    /// <summary>
    /// Every session the registry currently holds inside one instance.
    /// </summary>
    /// <remarks>
    /// Presence for the member list is exactly "this session is inside this
    /// instance and is still connected". The ownership fence is deliberately not
    /// consulted: it protects authoritative actions, and using it here would take
    /// a member who is physically inside the dungeon off the list - the operator's
    /// reported "entered and then disappeared" - whenever the fence the session
    /// was registered under and the fence the context carries differ.
    /// </remarks>
    private GameSessionContext[] LiveInstanceMembers(
        WorldInstanceId instanceId) =>
        [.. _sessions.Values
            .Where(context =>
                context.WorldInstanceId == instanceId &&
                context.CharacterId > 0 &&
                !context.Session.IsDisconnected)
            .GroupBy(static context => context.CharacterId)
            .Select(static group => group.First())
            .OrderBy(static context => context.CharacterId)];

    /// <summary>
    /// The run's own leader. It is transferable, so the panel follows the record
    /// the run keeps it in and republishes when it moves.
    /// </summary>
    private int InstanceRosterLeader(
        WorldInstanceId instanceId,
        InstanceRunKind kind) => kind switch
    {
        InstanceRunKind.Wonderland =>
            _wonderlandAdmissions.TryGetValue(instanceId, out var wonderland)
                ? wonderland.LeaderId
                : 0,
        InstanceRunKind.Atlantis =>
            _atlantisLeaderCharacterIds.GetValueOrDefault(instanceId),
        InstanceRunKind.HarborAttack =>
            _harborAttackAdmissions.TryGetValue(instanceId, out var harbor)
                ? harbor.LeaderId
                : 0,
        InstanceRunKind.Medusa =>
            _medusaLeaderUi.TryGetValue(instanceId, out var medusa)
                ? medusa.LeaderCharacterId
                : 0,
        InstanceRunKind.TrainingGround => TrainingGroundRosterLeader(instanceId), // TRAINING
        _ => 0
    };

    /// <summary>
    /// The members a run is still holding a live invitation or Enter window for:
    /// invited or registered, not inside yet, and inside their own sixty seconds.
    /// </summary>
    /// <remarks>
    /// Both waits are derived from records that already name the run they belong
    /// to - the Enter window's target instance and the invitation's target
    /// instance - so no run kind takes part in deciding who is waiting.
    /// </remarks>
    private Dictionary<int, InstanceRosterEntry> InstanceRunWaitingMembers(
        WorldInstanceId instanceId,
        DateTimeOffset now,
        IReadOnlyList<InstanceRosterEntry> recorded)
    {
        var recordedIds = new HashSet<int>(
            recorded.Select(static member => member.CharacterId));
        var waiting = new Dictionary<int, InstanceRosterEntry>();
        foreach (var window in _memberEntryWindows.Values)
        {
            if (window.Closed || window.ExpiresAt <= now)
            {
                continue;
            }

            // A window the run has not bound yet is the request that is creating
            // this very run: only the members the run already carries wait on it,
            // so a window that belongs to another run can never add a row here.
            var bound = window.TargetInstanceId is { } target;
            if (bound && window.TargetInstanceId != instanceId)
            {
                continue;
            }

            foreach (var member in window.Members)
            {
                // The leader is not waiting on his own window, and a member who
                // answered "no" is not coming.
                if (ReferenceEquals(member.Session, window.LeaderSession) ||
                    window.Declined.Contains(member.CharacterId) ||
                    !bound && !recordedIds.Contains(member.CharacterId))
                {
                    continue;
                }

                waiting[member.CharacterId] = ToRosterEntry(member);
            }
        }

        foreach (var invitation in _medusaInvitations.Values)
        {
            if (invitation.ExpiresAt <= now ||
                invitation.TargetWorldInstanceId != instanceId ||
                invitation.Invitee.CharacterId <= 0)
            {
                continue;
            }

            waiting[invitation.Invitee.CharacterId] = new(
                invitation.Invitee.CharacterId,
                invitation.Invitee.CharacterName,
                invitation.Invitee.Level,
                invitation.Invitee.Profession,
                invitation.Invitee.AccountId,
                invitation.Invitee.Ownership);
        }

        return waiting;
    }

    /// <summary>
    /// The published roster's identity. It carries the state as well as the
    /// presentation values, so a member starting to wait, coming in, dropping or
    /// taking the run's leadership republishes the roster to everyone inside.
    /// </summary>
    private static string InstanceRosterSignature(
        IReadOnlyList<RepetitionInstanceMember> roster) =>
        string.Join('|', roster.Select(static member =>
            $"{member.CharacterId}:{member.Name}:{member.Level}:" +
            $"{member.Profession}:{(byte)member.State}"));
}

/// <summary>
/// The one member record a running instance publishes its roster from: the party
/// the run was registered for, every member the shared admission path has pulled
/// in since, and which of them actually came in.
/// </summary>
/// <remarks>
/// There is one of these per run instance, written only by
/// <c>BeginInstanceRunMembership</c> (registration) and
/// <c>RecordInstanceRunMemberEntry</c> (a member being pulled in), plus the
/// roster snapshot's own observation of a member inside. Waiting is not stored:
/// it is derived from the live Enter windows and invitations, each of which names
/// the run it belongs to. Every member of the record is touched under the
/// registry gate.
/// </remarks>
internal sealed class InstanceRunMembership
{
    private readonly List<GameSessionRegistry.InstanceRosterEntry> _members = [];
    private readonly HashSet<int> _entrants = [];

    /// <summary>
    /// The run's roster, in publication order.
    /// </summary>
    public IReadOnlyList<GameSessionRegistry.InstanceRosterEntry> Members => _members;

    /// <summary>
    /// The characters the run recorded as having come in.
    /// </summary>
    public IReadOnlySet<int> Entrants => _entrants;

    /// <summary>
    /// Records a registered member once.
    /// </summary>
    public bool RecordMember(GameSessionRegistry.InstanceRosterEntry member)
    {
        if (member.CharacterId <= 0 ||
            _members.Any(existing => existing.CharacterId == member.CharacterId))
        {
            return false;
        }

        _members.Add(member);
        return true;
    }

    /// <summary>
    /// Records a member who has just been pulled into the run, as a member and as
    /// an entrant.
    /// </summary>
    public bool RecordEntrant(GameSessionRegistry.InstanceRosterEntry member)
    {
        if (member.CharacterId <= 0)
        {
            return false;
        }

        RecordMember(member);
        return _entrants.Add(member.CharacterId);
    }

    /// <summary>
    /// Records a member this snapshot has seen inside the run.
    /// </summary>
    public bool RecordEntrant(int characterId) =>
        characterId > 0 && _entrants.Add(characterId);
}
