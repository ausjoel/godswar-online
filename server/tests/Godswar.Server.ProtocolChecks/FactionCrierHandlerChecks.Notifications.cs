using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FactionCrierHandlerChecks
{
    private static async Task CheckTalentPointNoticeCommitAndReplayAsync()
    {
        const int awardedTalentPoints = 126;
        var before = CreateSnapshot(
            level: 80,
            BagWithAllNameplates(),
            after: false);
        var candidateAfter = CreateSnapshot(
            level: 80,
            GameDefaults.EmptyKitBag,
            after: true);
        var beforeCharacter = before.Character ??
            throw new InvalidOperationException("Missing before character.");
        var candidateCharacter = candidateAfter.Character ??
            throw new InvalidOperationException("Missing after character.");
        var after = candidateAfter with
        {
            Character = candidateCharacter with
            {
                Progression = candidateCharacter.Progression with
                {
                    Experience = beforeCharacter.Progression.Experience,
                    TalentPoints = checked(
                        beforeCharacter.Progression.TalentPoints +
                        awardedTalentPoints)
                }
            }
        };
        var receipt = CreateReceipt(
            before,
            after,
            FactionCrierOperation.TurnIn,
            subId: 111,
            nativeResultSubId: 2014,
            awardedTalentPoints: awardedTalentPoints);

        var executor = new FactionCrierExecutor
        {
            ExecuteResult = FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.Committed,
                receipt)
        };
        await using (var fixture = await CreateFixtureAsync(
                         secure: true,
                         before,
                         after,
                         executor))
        {
            await InvokeAsync(
                fixture.Handler,
                CreateOddTripleTalentPacket(OperationId));

            var packets = fixture.ReadLegacyPackets();
            var talentPackets = packets
                .Where(packet => ReadOpcode(packet) == 0x2845)
                .ToArray();
            Check.Equal(1, talentPackets.Length,
                "committed Talent-only turn-in shows one gain notice");
            Check.True(
                talentPackets[0].SequenceEqual(
                    Convert.FromHexString(
                        "0C004528050000007E000000")),
                "Talent Point notice carries type 5 and committed delta");
            Check.True(
                !packets.Any(packet => ReadOpcode(packet) == 0x272F),
                "Talent-only turn-in does not fabricate an EXP notice");

            var talentIndex = FindOpcode(packets, 0x2845);
            var statusIndex = FindOpcode(packets, 0x27B6);
            var bagIndex = FindOpcode(packets, 0x2731);
            var resultIndex = FindOpcode(
                packets,
                Opcodes.NpcFunctionActionResponse);
            Check.True(
                talentIndex >= 0 && talentIndex < statusIndex &&
                statusIndex < bagIndex && bagIndex < resultIndex,
                "Talent notice precedes authoritative status and result");
            Check.Equal(
                beforeCharacter.Progression.TalentPoints +
                    awardedTalentPoints,
                fixture.Character.TalentPoints,
                "Talent notice accompanies authoritative projected points");
        }

        var replayExecutor = new FactionCrierExecutor
        {
            ReplayResult = FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.Duplicate,
                receipt)
        };
        await using var replayFixture = await CreateFixtureAsync(
            secure: true,
            before,
            after,
            replayExecutor);
        await InvokeAsync(
            replayFixture.Handler,
            CreateOddTripleTalentPacket(OperationId));

        var replayPackets = replayFixture.ReadLegacyPackets();
        Check.True(
            !replayPackets.Any(packet => ReadOpcode(packet) == 0x2845),
            "durable replay never repeats the Talent Point notice");
        Check.Equal(0, replayExecutor.ExecuteCount,
            "durable Talent replay never executes the reward again");
        Check.True(
            replayFixture.SecureTransport!.CommandResults.Single()
                .Disposition == SecureLegacyCommandDisposition.Replayed,
            "durable Talent replay still settles through secure authority");
    }

    private static GamePacket CreateOddTripleTalentPacket(
        Guid operationId) =>
        CreateActionPacket(
            2,
            arguments =>
            {
                arguments[0] = 20;
                arguments[1] = 107;
                arguments[2] = 111;
            },
            operationId);
}
