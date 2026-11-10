using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentQueue<PendingMemberEntryJoin>
        _pendingMemberEntryJoins = [];

    /// <summary>
    /// The characters one instance is already admitting, so a repeated entry
    /// request cannot queue the same character twice.
    /// </summary>
    /// <remarks>
    /// The client resends its confirmation, and (for an invitation) follows the
    /// same scene frame again, so this is the one place that has to answer "is
    /// this character already on the way into this instance?" before a second
    /// reservation is minted for him. The entry is claimed under the registry
    /// gate and released by whichever of the three exits ends the join, so it
    /// lives exactly as long as the queued admission does and never blocks the
    /// retry an instance can legitimately demand afterwards.
    /// </remarks>
    private readonly ConcurrentDictionary<
        (int CharacterId, WorldInstanceId InstanceId), byte>
        _memberEntryJoinsInFlight = [];

    /// <summary>
    /// Accepts a member's confirmed Enter window and defers the transfer to the
    /// world tick, which performs it exactly the way a registered party member is
    /// admitted: the registry pulls the member in through its session sink.
    /// </summary>
    internal bool TryEnqueueMemberEntryJoin(
        ClientSession session,
        MemberEntryJoin join,
        Guid reservationId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (reservationId == Guid.Empty || !join.TargetInstanceId.IsValid ||
            join.TargetMapId == 0)
        {
            return false;
        }
        (int CharacterId, WorldInstanceId InstanceId) inFlight;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                context.Session.IsDisconnected ||
                context.WorldInstanceId != join.SourceWorldInstanceId ||
                context.WorldInstanceId == join.TargetInstanceId)
            {
                return false;
            }
            inFlight = (context.CharacterId, join.TargetInstanceId);
            if (!_memberEntryJoinsInFlight.TryAdd(inFlight, 0))
            {
                return false;
            }
        }
        _pendingMemberEntryJoins.Enqueue(
            new(session, join, reservationId, DateTimeOffset.UtcNow));
        return true;
    }

    /// <summary>
    /// Drives the queued member entries. Runs from the world tick, so no member's
    /// packet is in flight and the admission can take that member's state gate
    /// the way the registered-party transfer does.
    /// </summary>
    internal async Task AdvanceMemberEntryJoinsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = _pendingMemberEntryJoins.Count;
        for (var index = 0; index < pending; index++)
        {
            if (!_pendingMemberEntryJoins.TryDequeue(out var join))
            {
                return;
            }
            if (now - join.EnqueuedAt > TimeSpan.FromSeconds(60))
            {
                await ReleaseQueuedMemberEntryJoinAsync(join, "expired");
                continue;
            }

            AuthoritativeInstanceTransitionCommand command;
            InstanceRosterEntry member;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(join.Session, out var context) ||
                    context.Session.IsDisconnected ||
                    context.WorldInstanceId != join.Join.SourceWorldInstanceId ||
                    !context.Ownership.IsValid)
                {
                    _ = ReleaseQueuedMemberEntryJoinAsync(join, "session");
                    continue;
                }
                command = new AuthoritativeInstanceTransitionCommand(
                    context.CharacterId,
                    context.WorldInstanceId,
                    context.MapId,
                    context.Ownership,
                    join.Join.TargetInstanceId,
                    join.Join.TargetMapId,
                    join.Join.ArrivalX,
                    join.Join.ArrivalZ);
                member = new InstanceRosterEntry(
                    context.CharacterId,
                    context.CharacterName,
                    context.Character.Level,
                    context.Character.Profession);
            }

            bool moved;
            try
            {
                moved = await TransitionPartyMemberToAuthoritativeInstanceAsync(
                    join.Session,
                    command,
                    cancellationToken);
            }
            catch (Exception error) when (
                error is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    "[instance-entry] member admission fault: " + error.Message);
                moved = false;
            }
            if (!moved)
            {
                // One retry window: the member keeps the attempt they spent and
                // the next tick tries again, so a transient refusal does not cost
                // them the run.
                if (now - join.EnqueuedAt < TimeSpan.FromSeconds(30))
                {
                    _pendingMemberEntryJoins.Enqueue(join);
                }
                else
                {
                    await ReleaseQueuedMemberEntryJoinAsync(join, "refused");
                }
                continue;
            }

            await RecordMemberEntryAdmissionAsync(
                join.ReservationId,
                command.CharacterId,
                cancellationToken);
            // The member is inside the run now, so the run's one member record
            // takes him in: from here on the record - not the entry window - is
            // what keeps him on the roster, inside or dropped.
            RecordInstanceRunMemberEntry(join.Join.TargetInstanceId, member);
            // He is inside now, and the run's member record keeps him on the
            // roster; the admission no longer holds the seat.
            ForgetMemberEntryJoinInFlight(join.Session, join.Join.TargetInstanceId);
            // His window is spent: leaving it behind made his next entry of his
            // own look like a member confirmation for this finished run.
            ForgetMemberEntryWindow(join.Session);
            Console.WriteLine(
                "[instance-entry] member admitted character=" +
                $"{command.CharacterId} instance={join.Join.TargetInstanceId} " +
                $"map={join.Join.TargetMapId}");
        }
    }

    /// <summary>
    /// Reports whether this character is already on the way into that instance,
    /// so the same member's repeated confirmation does not spend a second
    /// reservation.
    /// </summary>
    internal bool IsMemberEntryJoinInFlight(
        ClientSession session,
        WorldInstanceId targetInstanceId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!targetInstanceId.IsValid)
        {
            return false;
        }
        lock (_gate)
        {
            return _sessions.TryGetValue(session, out var context) &&
                _memberEntryJoinsInFlight.ContainsKey(
                    (context.CharacterId, targetInstanceId));
        }
    }

    /// <summary>
    /// Drops the in-flight claim of a join that never reached the queue, so a
    /// refused enqueue leaves the member free to confirm again.
    /// </summary>
    internal void ForgetMemberEntryJoinInFlight(
        ClientSession session,
        WorldInstanceId targetInstanceId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!targetInstanceId.IsValid)
        {
            return;
        }
        lock (_gate)
        {
            if (_sessions.TryGetValue(session, out var context))
            {
                _memberEntryJoinsInFlight.TryRemove(
                    (context.CharacterId, targetInstanceId),
                    out _);
            }
        }
    }

    private ILegacyInstanceDailyEntryClaimStore? MemberEntryAdmissionStore =>
        _wonderlandTitleAdmissions ?? _atlantisCompletionAdmissions;

    private async Task RecordMemberEntryAdmissionAsync(
        Guid reservationId,
        int characterId,
        CancellationToken cancellationToken)
    {
        if (MemberEntryAdmissionStore is { } store)
        {
            await store.RecordAdmissionsAsync(
                reservationId,
                new[] { characterId },
                cancellationToken);
            return;
        }
        ReleaseLocalLegacyInstanceDailyEntryMembers(
            reservationId,
            new[] { characterId });
    }

    private async Task ReleaseQueuedMemberEntryJoinAsync(
        PendingMemberEntryJoin join,
        string reason)
    {
        if (MemberEntryAdmissionStore is { } store)
        {
            await store.ReleaseAsync(join.ReservationId, CancellationToken.None);
        }
        else
        {
            ReleaseLocalLegacyInstanceDailyEntry(join.ReservationId);
        }
        // The join is over, so the character may be admitted again.
        ForgetMemberEntryJoinInFlight(join.Session, join.Join.TargetInstanceId);
        // A released join also ends the window it came from: a stale record would
        // make this member's next entry of his own look like a confirmation here.
        ForgetMemberEntryWindow(join.Session);
        Console.Error.WriteLine(
            "[instance-entry] member join released reason=" + reason +
            $" reservation={join.ReservationId}");
    }

    private readonly record struct PendingMemberEntryJoin(
        ClientSession Session,
        MemberEntryJoin Join,
        Guid ReservationId,
        DateTimeOffset EnqueuedAt);

    /// <summary>
    /// Hands one named character the same Enter window the party received at
    /// registration, on behalf of a member who is already inside a running
    /// instance.
    /// </summary>
    /// <remarks>
    /// The window is the registration window: the invited session gets the same
    /// 10222 + 10216 pair and the same sixty seconds, and its confirmation then
    /// travels the identical path (its own daily attempt, the authoritative
    /// transfer, the run's roster). Nothing here mints an identifier or asks the
    /// client to answer differently, so no invitation bookkeeping is needed: the
    /// window record itself is the invitation.
    /// </remarks>
    internal async Task<bool> PublishInstanceInvitationAsync(
        ClientSession inviterSession,
        string inviteeName,
        InstanceCallerEntryDestination destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inviterSession);
        if (string.IsNullOrWhiteSpace(inviteeName) || destination.ClientSceneId <= 0)
        {
            return false;
        }

        ClientSession inviteeSession;
        int inviteeCharacterId;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(inviterSession, out var inviter) ||
                inviter.Session.IsDisconnected ||
                !TryFindOnlinePartyMemberLocked(
                    inviteeName,
                    inviter.RealmId,
                    out var invitee) ||
                invitee.CharacterId == inviter.CharacterId ||
                !IsCurrentInvitingInstanceMemberLocked(
                    inviter,
                    destination.Kind,
                    out var instanceId) ||
                invitee.WorldInstanceId == instanceId)
            {
                return false;
            }

            // Bound immediately: the run exists, so a confirmation joins it
            // directly instead of waiting for a leader's own commit.
            var member = new LegacyInstancePartyMember(
                invitee.Session,
                invitee.AccountId,
                invitee.CharacterId,
                invitee.CharacterName,
                invitee.Character.Level,
                invitee.Character.Profession,
                invitee.RealmId,
                invitee.WorldInstanceId,
                invitee.MapId,
                invitee.Ownership);
            if (_memberEntryWindows.TryGetValue(
                    invitee.Session,
                    out var previous))
            {
                CloseMemberEntryWindowLocked(previous);
            }
            var window = new MemberEntryWindow(
                inviterSession,
                destination.ClientSceneId,
                destination.Kind,
                [member],
                DateTimeOffset.UtcNow + MemberEntryWindowLifetime)
            {
                TargetInstanceId = instanceId,
                TargetMapId = destination.TargetMapId,
                TargetArrivalX = destination.TargetX,
                TargetArrivalZ = destination.TargetZ
            };
            _memberEntryWindows[invitee.Session] = window;
            inviteeSession = invitee.Session;
            inviteeCharacterId = invitee.CharacterId;
        }

        try
        {
            await inviteeSession.SendAsync(
                PacketBuilder.InstanceEntryQueueState(destination.ClientSceneId),
                cancellationToken,
                "InstanceInvitationQueued");
            await inviteeSession.SendAsync(
                PacketBuilder.InstanceEntryNotice(destination.ClientSceneId),
                cancellationToken,
                "InstanceInvitationCountdown");
        }
        catch (Exception error) when (
            error is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "[instance-invite] delivery failed invitee=" +
                $"{inviteeName}: {error.Message}");
            lock (_gate)
            {
                if (_memberEntryWindows.TryGetValue(
                        inviteeSession,
                        out var window))
                {
                    CloseMemberEntryWindowLocked(window);
                }
            }
            return false;
        }

        Console.WriteLine(
            "[instance-invite] invited invitee=" + inviteeName +
            $" character={inviteeCharacterId} scene={destination.ClientSceneId} " +
            $"destination={destination.Kind} instances=" +
            $"{_memberEntryWindows.Count}");
        return true;
    }

    /// <summary>
    /// Whether the actor is inside a live run of that instance right now. Any
    /// member may invite, so this asks for membership rather than leadership.
    /// </summary>
    private bool IsCurrentInvitingInstanceMemberLocked(
        GameSessionContext actor,
        InstanceCallerEntryKind kind,
        out WorldInstanceId instanceId)
    {
        instanceId = actor.WorldInstanceId;
        return kind switch
        {
            InstanceCallerEntryKind.HarborAttack =>
                _harborAttackAdmissions.ContainsKey(actor.WorldInstanceId),
            var trainingKind when TrainingGroundPolicy.IsTrainingGroundKind(trainingKind) => // TRAINING / WARRIOR
                IsTrainingGroundAdmission(actor.WorldInstanceId),
            InstanceCallerEntryKind.Atlantis =>
                IsCurrentAtlantisInvitingMemberLocked(actor),
            InstanceCallerEntryKind.Wonderland =>
                _wonderlandAdmissions.TryGetValue(
                    actor.WorldInstanceId,
                    out var wonderland) &&
                wonderland.Entrants.Contains(actor.CharacterId),
            _ => false
        };
    }

    private bool IsCurrentAtlantisInvitingMemberLocked(
        GameSessionContext actor)
    {
        if (actor.MapId != DynamicDungeonContentMapPolicy.AtlantisPortalMapId)
        {
            return false;
        }
        if (!TryGetAtlantisEncounterSnapshot(
                actor.WorldInstanceId,
                out var atlantis))
        {
            return false;
        }
        return atlantis.State == AtlantisRunState.Active;
    }
}
