using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The in-instance "invite by name" request (10224, client to server): a
    /// member who is already inside names another character, who then receives
    /// the party's own Enter window.
    /// </summary>
    /// <remarks>
    /// 美杜莎之岛 is the one instance whose confirmation is not that Enter window:
    /// its client scene (209 advanced, 223 normal) belongs to the run's native
    /// invitation, which carries an invitation identity and is answered on 10217.
    /// The shared handler claimed 10224 for every instance and dropped those
    /// scenes as malformed, so a Medusa member could no longer invite anyone by
    /// name. This routes them back to the run's own invitation instead; the
    /// scenes the Enter window serves, and the unknown scenes that are still
    /// refused, are unchanged.
    /// </remarks>
    private async Task HandleInstanceInvitationRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Opcode != Opcodes.RepetitionInvitation ||
            packet.Length != 44 ||
            packet.Buffer.Length != 44 ||
            packet.Payload.Length != 40 ||
            _character is null)
        {
            return;
        }

        // The client's own frame: payload +0 is the target instance's client
        // scene, +4 is the invitation identity it leaves at zero, and the
        // invitee's name is the NUL-terminated start of a thirty-two byte field
        // at +8. Everything after the terminator is uninitialized client memory.
        var clientSceneId = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload);
        var inviteeName = ReadInvitationName(
            packet.Payload.Slice(sizeof(int) * 2, 32));
        if (clientSceneId <= 0 || inviteeName.Length == 0)
        {
            LogRefusedInstanceInvitation(clientSceneId, inviteeName);
            return;
        }

        if (MedusaIslandRosterPolicy.IsClientScene(clientSceneId))
        {
            await HandleMedusaInvitationByNameAsync(
                checked((short)clientSceneId),
                inviteeName,
                cancellationToken);
            return;
        }

        if (!InstanceCallerProtocol.TryResolveInvitedInstance(
                clientSceneId,
                out var destination))
        {
            LogRefusedInstanceInvitation(clientSceneId, inviteeName);
            return;
        }

        if (!await _registry.PublishInstanceInvitationAsync(
                _session,
                inviteeName,
                destination,
                cancellationToken))
        {
            Console.WriteLine(
                "[instance-invite] refused character=" + _character.Name +
                $" invitee={inviteeName} scene={clientSceneId}");
        }
    }

    /// <summary>
    /// The 美杜莎之岛 name invitation: the requester must be inside an active run
    /// of the scene he named, and the invitee then receives that run's own
    /// confirmation, exactly as a party member's automatic one.
    /// </summary>
    private async Task HandleMedusaInvitationByNameAsync(
        short clientSceneId,
        string inviteeName,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryBeginManualMedusaInvitation(
                _session,
                inviteeName,
                clientSceneId,
                DateTimeOffset.UtcNow,
                out var invitation))
        {
            Console.WriteLine(
                "[instance-invite] refused Medusa invitation character=" +
                $"{_character?.Name ?? "<none>"} invitee={inviteeName} " +
                $"scene={clientSceneId}");
            return;
        }

        if (!await PublishMedusaInvitationNoticeAsync(
                invitation,
                cancellationToken))
        {
            _registry.CancelMedusaInvitation(invitation.InvitationId);
            return;
        }

        _ = MonitorMedusaInvitationTimeoutAsync(
            invitation,
            CancellationToken.None);
        Console.WriteLine(
            "[instance-invite] invited invitee=" + inviteeName +
            $" character={invitation.Invitee.CharacterId} " +
            $"scene={clientSceneId} destination=Medusa instances=" +
            $"{invitation.TargetWorldInstanceId}");
    }

    private void LogRefusedInstanceInvitation(
        int clientSceneId,
        string inviteeName)
    {
        Console.Error.WriteLine(
            "[instance-invite] rejected malformed request character=" +
            $"{_character?.Name ?? "<none>"} scene={clientSceneId} " +
            $"name='{inviteeName}'");
    }

    /// <summary>
    /// The client writes the invitee's name into a fixed-width field and does not
    /// clear the rest of it, so only the bytes before the terminator are the
    /// name; the remainder is uninitialized client memory.
    /// </summary>
    private static string ReadInvitationName(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        var length = terminator < 0 ? field.Length : terminator;
        var text = new StringBuilder(length);
        for (var index = 0; index < length; index++)
        {
            var value = field[index];
            if (value is < 0x20 or > 0x7E)
            {
                break;
            }
            text.Append((char)value);
        }
        return text.ToString().Trim();
    }
}
