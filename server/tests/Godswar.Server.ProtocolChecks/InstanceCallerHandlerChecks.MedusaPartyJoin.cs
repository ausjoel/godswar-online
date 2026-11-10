using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string MedusaPartyJoinCheckName =
        "美杜莎之岛 does not offer entry for a party join";

    /// <summary>
    /// The operator's rule: being in a party is not being invited into the run.
    /// Accepting a party invitation must publish no 美杜莎 confirmation, and the
    /// member must keep his own daily attempt and stay outside; naming him from
    /// inside the instance (the 10224 invitation) is still what brings him in.
    /// </summary>
    public static async Task RunMedusaPartyJoinAsync() =>
        await CheckPartyJoinOffersNoMedusaEntryAsync();

    private static async Task CheckPartyJoinOffersNoMedusaEntryAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            null,
            null,
            partySize: 2,
            joinParty: false);
        var leader = fixture.Leader;
        var member = fixture.Followers.Single();
        leader.Registry.RegisterInstanceTransitionSink(
            member.Session,
            (command, token) =>
                InvokePartyTransitionAsync(member.Handler, command, token));

        await OpenMedusaPageAsync(leader);
        await InvokeAsync(
            leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));
        var runInstanceId = GetSourceInstanceId(leader);
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        Check.True(
            leader.Character.CurrentMap == 200 &&
            member.Character.CurrentMap == leader.SourceMapId,
            "the party-join fixture starts with the inviting member inside the " +
            "run and the other one outside");

        var memberBefore = member.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            leader.Handler,
            new GamePacket(PacketBuilder.PartyAction(
                Opcodes.PartyInvite,
                0x1448,
                leader.Character.Name,
                member.Character.Name)));
        await InvokeAsync(
            member.Handler,
            new GamePacket(PacketBuilder.PartyAction(
                Opcodes.PartyAccept,
                0x1448,
                leader.Character.Name,
                member.Character.Name)));
        // The removed auto-offer published the confirmation from a fire-and-forget
        // task; a tick here gives any such task room to run before it is read.
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var memberPackets = member.Transport.ReadLegacyPackets()
            .Skip(memberBefore)
            .ToArray();
        Check.True(
            leader.Registry.GetPartyMembership(leader.Session) is
            {
                IsLeader: true,
                MemberCharacterIds.Count: 2
            } &&
            leader.Registry.TryGetSessionWorldInstanceId(
                member.Session,
                out var memberInstanceId) &&
            memberInstanceId != runInstanceId &&
            member.Character.CurrentMap == leader.SourceMapId &&
            !memberPackets.Any(IsRepetitionInvitation) &&
            !leader.Registry.IsMemberEntryWindowMember(
                member.Session,
                MedusaIslandRosterPolicy.EnhancedClientSceneId),
            "accepting the party invitation publishes neither a Medusa " +
            "confirmation nor an Enter window, and the member stays outside " +
            "with his own daily attempt");

        // The invitation the operator kept: naming him from inside the run.
        await InvokeAsync(
            leader.Handler,
            CreateInvitationByNamePacket(
                MedusaIslandRosterPolicy.EnhancedClientSceneId,
                member.Character.Name));
        var notice = member.Transport.ReadLegacyPackets()
            .Skip(memberBefore)
            .Single(IsRepetitionInvitation);
        var (repetitionId, invitationId) =
            ReadRepetitionInvitation(notice);
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
                out var admittedInstanceId) &&
            admittedInstanceId == runInstanceId &&
            member.Character.CurrentMap == 200 &&
            leader.Registry.IsMedusaCharacterAdmitted(
                runInstanceId,
                member.Character.Id),
            "naming the member from inside the run still brings him into that " +
            "same run");
    }
}
