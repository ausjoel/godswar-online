using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string ManualMedusaInvitationCheckName =
        "美杜莎之岛 invite-by-name from inside the run";

    /// <summary>
    /// 美杜莎之岛's own "invite by name": the run's confirmation is the native
    /// 10224 frame's answer, not the shared Enter window, so a member inside the
    /// scene may still name anyone and have that character confirm into the same
    /// run.
    /// </summary>
    public static async Task RunManualMedusaInvitationAsync() =>
        await CheckManualMedusaInvitationByNameAsync();

    private static async Task CheckManualMedusaInvitationByNameAsync()
    {
        // The inviter enters alone (美杜莎's leader admission validates the party
        // it was registered for), and the character he names is online in the same
        // realm but in no party of his own.
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            null,
            null,
            partySize: 2,
            joinParty: false);
        var leader = fixture.Leader;
        var member = fixture.Followers.Single();
        // The fixture wires the followers' shared (authoritative) transition sink;
        // 美杜莎's own invitation is admitted through the party transition the run
        // uses, so the invitee needs that sink as well.
        leader.Registry.RegisterInstanceTransitionSink(
            member.Session,
            (command, token) =>
                InvokePartyTransitionAsync(
                    member.Handler,
                    command,
                    token));

        await OpenMedusaPageAsync(leader);
        await InvokeAsync(
            leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));
        var targetInstanceId = GetSourceInstanceId(leader);
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        Check.True(
            leader.Character.CurrentMap == 200 &&
            member.Character.CurrentMap == leader.SourceMapId &&
            leader.Registry.IsMedusaCharacterAdmitted(
                targetInstanceId,
                leader.Character.Id),
            "the inviting member is inside the Medusa run");

        var memberBefore = member.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            leader.Handler,
            CreateInvitationByNamePacket(
                MedusaIslandRosterPolicy.EnhancedClientSceneId,
                member.Character.Name));

        var memberPackets = member.Transport.ReadLegacyPackets()
            .Skip(memberBefore)
            .ToArray();
        Check.True(
            memberPackets.Count(IsRepetitionInvitation) == 1,
            "naming a character from inside 美杜莎之岛 publishes exactly one " +
            "Medusa confirmation");
        var notice = memberPackets.Single(IsRepetitionInvitation);
        var (repetitionId, invitationId) =
            ReadRepetitionInvitation(notice);
        Check.True(
            repetitionId ==
                MedusaIslandRosterPolicy.EnhancedClientSceneId &&
            invitationId > 0 &&
            PacketText.ReadFixedAscii(
                notice,
                12,
                CharacterSnapshotLimits.CharacterNameLength) ==
                leader.Character.Name,
            "the manual confirmation carries the run's own scene, a " +
            "server-minted invitation identity, and the inviter's name");

        await InvokeAsync(
            member.Handler,
            CreateRepetitionResponse(
                repetitionId,
                invitationId,
                accepted: true),
            confirmEntry: false);

        Check.True(
            leader.Registry.TryGetSessionWorldInstanceId(
                member.Session,
                out var memberInstanceId) &&
            memberInstanceId == targetInstanceId &&
            member.Character.CurrentMap == 200 &&
            leader.Registry.IsMedusaCharacterAdmitted(
                targetInstanceId,
                member.Character.Id),
            "a character invited by name joins the inviting member's own run " +
            "and gains combat admission");

        var memberBeforeForeignScene =
            member.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            leader.Handler,
            CreateInvitationByNamePacket(
                InstanceCallerProtocol.AtlantisClientSceneId,
                member.Character.Name));
        Check.True(
            member.Transport.ReadLegacyPackets().Count ==
                memberBeforeForeignScene,
            "a scene the inviter is not running in still yields no invitation");
    }

    /// <summary>
    /// The stock 10224 frame: the target scene at +4, the invitation identity the
    /// client leaves at zero at +8, and the invitee's name in the fixed-width
    /// field at +12.
    /// </summary>
    private static GamePacket CreateInvitationByNamePacket(
        int clientSceneId,
        string inviteeName)
    {
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.RepetitionInvitation);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(4),
            clientSceneId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 0);
        PacketText.WriteFixedAscii(
            bytes.AsSpan(12, CharacterSnapshotLimits.CharacterNameLength),
            inviteeName);
        return new GamePacket(bytes);
    }
}
