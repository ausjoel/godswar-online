using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// The 美杜莎之岛 invitation an in-instance member sent by name: the native
    /// 10224 request names another character, and that character receives the
    /// run's own confirmation - scene, invitation identity and inviter name - so
    /// his answer travels the same 10217 path every other Medusa confirmation
    /// does.
    /// </summary>
    /// <remarks>
    /// This is the manual invitation the run had before the shared "invite by
    /// name" handler claimed 10224, and it is deliberately built out of the
    /// pieces that already exist rather than a second flow: the requester is
    /// captured exactly as <c>TryBeginLateMedusaInvitation</c> captures the
    /// leader who is inside a run, the invitation is registered in the one
    /// Medusa invitation table, its notice is published by the one publisher, and
    /// its acceptance is admitted by the one late-admission path
    /// (<c>AdmitInvitedMedusaMemberAsync</c>).
    /// <para>
    /// The party snapshot such an invitation carries is the requester and the
    /// invitee, with no party identity: the 10224 name invitation is not a party
    /// operation, and a null party identity is what tells the invitation
    /// validator there is no party membership to re-check when the invitee
    /// answers (see <c>ValidateMedusaInviteeLocked</c>).
    /// </para>
    /// <para>
    /// Validation is two-phase on purpose: the invitee's own state is captured
    /// under the gate, then the run is read from its world owner (never while the
    /// gate is held), and the same capture is repeated before the invitation is
    /// registered so a session that moved in between cannot be invited into a run
    /// it is no longer eligible for.
    /// </para>
    /// </remarks>
    internal bool TryBeginManualMedusaInvitation(
        ClientSession inviterSession,
        string inviteeName,
        short clientSceneId,
        DateTimeOffset now,
        out MedusaInstanceInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(inviterSession);
        invitation = null!;
        if (string.IsNullOrWhiteSpace(inviteeName) || clientSceneId <= 0)
        {
            return false;
        }

        ClientSession inviteeSession;
        WorldInstanceId targetWorldInstanceId;
        MedusaInstancePartySnapshot party;
        MedusaInstancePartyMember invitee;
        lock (_gate)
        {
            if (!TryCaptureManualMedusaInvitationLocked(
                    inviterSession,
                    inviteeName,
                    out inviteeSession,
                    out targetWorldInstanceId,
                    out party,
                    out invitee))
            {
                return false;
            }
        }

        if (!WorldInstances.TryFind(
                targetWorldInstanceId,
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
            ownership.Run.State != MedusaRunState.Active ||
            !ownership.Run.AdmittedCharacterIds.Contains(
                party.LeaderCharacterId) ||
            ownership.Run.AdmittedCharacterIds.Contains(
                invitee.CharacterId) ||
            ownership.Run.AdmittedCharacterIds.Count >=
                runtime.Descriptor.PlayerCapacity ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                ownership.ContentMapId.Value,
                out var clientSceneIdOfRun) ||
            clientSceneIdOfRun != clientSceneId)
        {
            return false;
        }

        lock (_gate)
        {
            if (!TryCaptureManualMedusaInvitationLocked(
                    inviterSession,
                    inviteeName,
                    out var currentInviteeSession,
                    out var currentTarget,
                    out party,
                    out invitee) ||
                !ReferenceEquals(currentInviteeSession, inviteeSession) ||
                currentTarget != targetWorldInstanceId)
            {
                return false;
            }
            if (_medusaInvitationBySession.ContainsKey(inviteeSession))
            {
                return false;
            }

            var invitationId = NextMedusaInvitationIdLocked();
            invitation = new(
                clientSceneId,
                invitationId,
                ownership.Difficulty,
                party,
                invitee,
                targetWorldInstanceId,
                now + MedusaInvitationLifetime);
            _medusaInvitations.Add(invitationId, invitation);
            _medusaInvitationBySession.Add(
                inviteeSession,
                invitationId);
            return true;
        }
    }

    /// <summary>
    /// Captures the requester as the invitation's authority and the named
    /// character as its invitee, entirely from current session state.
    /// </summary>
    private bool TryCaptureManualMedusaInvitationLocked(
        ClientSession inviterSession,
        string inviteeName,
        out ClientSession inviteeSession,
        out WorldInstanceId targetWorldInstanceId,
        out MedusaInstancePartySnapshot partySnapshot,
        out MedusaInstancePartyMember inviteeSnapshot)
    {
        inviteeSession = null!;
        targetWorldInstanceId = default;
        partySnapshot = null!;
        inviteeSnapshot = null!;

        if (!_sessions.TryGetValue(inviterSession, out var inviter) ||
            inviter.Session.IsDisconnected ||
            inviter.MapId is not (200 or 204) ||
            inviter.WorldInstanceId == default ||
            !inviter.Ownership.IsValid ||
            !IsCurrentAccountSession(
                inviter.AccountId,
                inviter.Session,
                inviter.Ownership) ||
            !TryFindOnlinePartyMemberLocked(
                inviteeName,
                inviter.RealmId,
                out var invitee) ||
            invitee.CharacterId == inviter.CharacterId ||
            ReferenceEquals(invitee.Session, inviterSession) ||
            invitee.WorldInstanceId == inviter.WorldInstanceId)
        {
            return false;
        }

        partySnapshot = new(
            PartyId: null,
            inviter.RealmId,
            inviter.CharacterId,
            [ToMedusaInvitationMember(inviter), ToMedusaInvitationMember(invitee)]);
        inviteeSnapshot = ToMedusaInvitationMember(invitee);
        inviteeSession = invitee.Session;
        targetWorldInstanceId = inviter.WorldInstanceId;
        return true;
    }

    /// <summary>
    /// The invitation's own copy of a member's identity and entry state, so a
    /// confirmation that arrives later can be checked against what the invitee
    /// was when he was invited.
    /// </summary>
    private static MedusaInstancePartyMember ToMedusaInvitationMember(
        GameSessionContext context) => new(
        context.Session,
        context.AccountId,
        context.CharacterId,
        context.CharacterName,
        context.Character.Level,
        context.Character.Profession,
        context.RealmId,
        context.WorldInstanceId,
        context.MapId,
        context.Ownership);
}
