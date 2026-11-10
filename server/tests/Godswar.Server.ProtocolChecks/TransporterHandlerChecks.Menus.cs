using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TransporterHandlerChecks
{
    private static async Task CheckNativeOpenAndMenusAsync()
    {
        foreach (var endpoint in Endpoints())
        {
            await using var fixture = await CreateFixtureAsync(
                endpoint,
                level: 140);

            await InvokeAsync(
                fixture.Handler,
                CreateDialogOpenPacket(endpoint.InteractionId));
            var openPackets = fixture.ReadPackets();
            Check.True(
                openPackets is [var open] &&
                open.SequenceEqual(PacketBuilder.NpcDialogOpenAck(
                    endpoint.InteractionId,
                    TransporterProtocol.DialogIndex,
                    endpoint.NpcKey)),
                $"{endpoint.NpcKey} emits its native dialog-open ACK");

            await InvokeAsync(
                fixture.Handler,
                CreateActionPacket(
                    endpoint.InteractionId,
                    TransporterProtocol.InitialRequestSubId));
            var menuPackets = fixture.ReadPackets().Skip(1).ToArray();
            Check.True(
                menuPackets is [var menu] &&
                menu.SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                    endpoint.InteractionId,
                    TransporterProtocol.DialogIndex,
                    [
                        .. endpoint.Menu,
                        .. TransporterProtocol.GetExtensionMenu(endpoint.NpcKey, endpoint.InteractionId)
                    ])),
                $"{endpoint.NpcKey} emits its endpoint-specific native menu");
        }
    }

    private static async Task CheckLevelRequirementResultsAsync()
    {
        await CheckLevelRequirementAsync(
            Sparta(),
            level: 39,
            subId: 2,
            TransporterProtocol.SpartaLevelRequirementResultSubId);
        await CheckLevelRequirementAsync(
            Athens(),
            level: 39,
            subId: 5,
            TransporterProtocol.AthensLevelRequirementResultSubId);
        await CheckLevelRequirementAsync(
            Sparta(),
            level: 89,
            subId: 9,
            TransporterProtocol.SpartaLevelRequirementResultSubId);
        await CheckLevelRequirementAsync(
            Sparta(),
            level: 89,
            subId: 10,
            TransporterProtocol.SpartaLevelRequirementResultSubId);
    }

    private static async Task CheckLevelRequirementAsync(
        Endpoint endpoint,
        int level,
        int subId,
        int expectedResultSubId)
    {
        await using var fixture = await CreateFixtureAsync(endpoint, level);
        await IssueTransporterMenuAsync(fixture, endpoint);
        var packetCount = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(endpoint.InteractionId, subId));

        Check.True(
            fixture.ReadPackets().Skip(packetCount).ToArray() is [var result] &&
            result.Length == 96 &&
            result.SequenceEqual(PacketBuilder.CapturedNpcFunctionActionResponse(
                endpoint.InteractionId,
                TransporterProtocol.ResultDialogIndex,
                expectedResultSubId)) &&
            fixture.Character.CurrentMap == endpoint.SourceMapId &&
            fixture.Store.PositionWrites.Count == 0,
            $"{endpoint.NpcKey} rejects level {level} through the native " +
            $"96-byte dialog 2/sub-ID {expectedResultSubId} result without " +
            "moving the character");
    }
}
