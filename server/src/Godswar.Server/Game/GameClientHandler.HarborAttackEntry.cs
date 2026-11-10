using Godswar.Server.Application.Npc;
using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// A party member's answer to the Enter window the leader's request published
    /// for them (or the window an in-instance member invited them with): the same
    /// zero-token 10217 the leader's own window sends, so it is claimed here
    /// before the countdown and invitation readers.
    /// </summary>
    private async Task<bool> TryHandleMemberEntryWindowResponseAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Opcode != Opcodes.RepetitionResponse ||
            packet.Length != 16 ||
            packet.Buffer.Length != 16 ||
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.Payload.Slice(sizeof(int), sizeof(int))) != 0)
        {
            return false;
        }

        var clientSceneId = BinaryPrimitives.ReadInt32LittleEndian(packet.Payload);
        var response = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(sizeof(int) * 2, sizeof(int)));
        if (response is not (0 or 1) ||
            !_registry.IsMemberEntryWindowMember(
                _session,
                clientSceneId))
        {
            return false;
        }

        if (response == 0)
        {
            // The member is not coming: the window stays open for the rest of the
            // party, and the published roster stops showing him as waiting.
            _registry.RecordMemberEntryDeclined(_session);
            await _session.SendAsync(
                PacketBuilder.RepetitionReset(),
                cancellationToken,
                "MemberEntryDeclined");
            return true;
        }

        if (_registry.TryConfirmMemberEntryWindow(
                _session,
                clientSceneId,
                out var join))
        {
            await BeginMemberEntryJoinAsync(join, cancellationToken);
            return true;
        }

        Console.WriteLine(
            "[instance-entry] entry confirmed character=" +
            $"{_character?.Name ?? "<none>"} scene={clientSceneId}");
        return true;
    }

    /// <summary>
    /// Spends the member's own daily attempt and hands the transfer to the same
    /// server-driven admission the registered party uses.
    /// </summary>
    /// <remarks>
    /// The transfer is deliberately not performed here. A registered member is
    /// pulled into the instance by the server (registry to session sink), which
    /// is the proven route; running it from this member's own packet would hold
    /// this session's state gate while the admission asks for it again, and would
    /// freeze the request's source identity at click time. The world tick drives
    /// it instead, from the member's current session state.
    /// </remarks>
    private async Task BeginMemberEntryJoinAsync(
        MemberEntryJoin join,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null || _session.IsDisconnected)
        {
            return;
        }

        // INSTANCES: a switch closed after the window opened refuses the join
        // before any daily entry is claimed.
        if (join.Kind == InstanceCallerEntryKind.HarborAttack &&
            !InstanceEntrySwitches.IsHarborAttackOpen)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(InstanceEntrySwitches.HarborAttackClosedNote),
                cancellationToken,
                "MemberEntryInstanceClosed");
            return;
        }
        // BAY2: a late joiner must be level 50-90 too, before any claim.
        var (harborMinimum, harborMaximum) = LevelBandGates.Apply( // FINALISE: editor band
            LevelBandGates.HarborAttack,
            InstanceCallerProtocol.HarborAttackMinimumLevel,
            InstanceCallerProtocol.HarborAttackMaximumLevel);
        if (join.Kind == InstanceCallerEntryKind.HarborAttack &&
            (_character.Level < harborMinimum || _character.Level > harborMaximum))
        {
            await _session.SendAsync(
                PacketBuilder.PersonalNotice(
                    System.Text.Encoding.ASCII.GetBytes(HarborAttackLevelBandNote)),
                cancellationToken,
                "MemberEntryLevelBand");
            return;
        }

        if (TrainingGroundPolicy.IsTrainingGroundKind(join.Kind) && // TRAINING / WARRIOR (F-51)
            !InstanceEntrySwitches.IsOpen(join.Kind))
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(join.Kind == InstanceCallerEntryKind.TrainingGround
                    ? InstanceEntrySwitches.TrainingGroundClosedNote
                    : InstanceEntrySwitches.WarriorTrainingClosedNote),
                cancellationToken,
                "MemberEntryInstanceClosed");
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var reservationId = Guid.NewGuid();
        if (join.Kind == InstanceCallerEntryKind.HarborAttack)
        {
            // INSTANCES: this reservation belongs to the run, so a restart refund
            // gives it back too (ordered before any close of the run).
            _registry.RecordHarborAttackMemberReservation(
                join.TargetInstanceId, reservationId, _character.Id);
        }
        LegacyInstanceDailyEntryClaimResult? claim;
        try
        {
            claim = await TryClaimLegacyInstanceDailyEntryAsync(
                reservationId,
                join.RealmId,
                _realmCalendar.GetDay(startedAt),
                join.Kind,
                new[] { _character.Id },
                startedAt,
                cancellationToken);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-entry] member claim fault character=" +
                $"{_character.Name}: {error.Message}");
            return;
        }
        if (claim is null ||
            claim.Status != LegacyInstanceDailyEntryClaimStatus.Claimed)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(
                    "You have already used today's entry for this instance."),
                cancellationToken,
                "MemberEntryNoAttempt");
            return;
        }

        // 飘渺 sealed its roster when the leader entered and planned its monsters
        // from that roster, so a slower member is added to the run's own lists
        // before the admission gate reads them - without touching the monsters
        // that are already published.
        if (join.Kind == InstanceCallerEntryKind.Wonderland &&
            !_registry.TryAdmitWonderlandLateEntrant(
                join.TargetInstanceId,
                _session,
                _character.Id,
                _character.Level,
                _character.Camp,
                reservationId))
        {
            await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
            Console.Error.WriteLine(
                "[instance-entry] wonderland roster append refused character=" +
                _character.Name);
            return;
        }

        if (!_registry.TryEnqueueMemberEntryJoin(
                _session,
                join,
                reservationId))
        {
            // Whether the queue refused the join or the character is already on
            // the way in, the reservation this confirmation just minted is not
            // the one he travels on, so it is given straight back.
            _registry.ForgetMemberEntryJoinInFlight(
                _session,
                join.TargetInstanceId);
            await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
            Console.Error.WriteLine(
                "[instance-entry] member join refused character=" +
                _character.Name);
            return;
        }

        Console.WriteLine(
            "[instance-entry] member join queued character=" + _character.Name +
            $" instance={join.TargetInstanceId} map={join.TargetMapId} " +
            $"reservation={reservationId}");
    }
}
