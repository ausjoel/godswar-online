using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class DuelArenaTransporterHandlerChecks
{
    private static async Task CheckSequentialRoundTripAsync()
    {
        var gatekeeper = Gatekeeper();
        var doorkeeper = Doorkeeper();
        await using var fixture = await CreateFixtureAsync(gatekeeper);

        await IssueMenuAsync(fixture, gatekeeper);
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(gatekeeper.InteractionId));
        Check.True(
            fixture.Store.PositionWrites is [var lower] &&
            lower.MapId == DuelArenaTransporterProtocol.MapId &&
            lower.X == DuelArenaTransporterProtocol.LowerArrivalX &&
            lower.Z == DuelArenaTransporterProtocol.LowerArrivalZ,
            "Arena_003 moves the round-trip actor from the upper lobby " +
            "to the reviewed lower arrival");

        await CompleteRoundTripHandoffAsync(fixture, "lower arena");

        fixture.Character.PositionX = doorkeeper.Spawn.X;
        fixture.Character.PositionZ = doorkeeper.Spawn.Z;
        fixture.Registry.UpdateCharacter(
            fixture.Session,
            fixture.Character,
            advanceWorldRevision: false);

        await IssueMenuAsync(fixture, doorkeeper);
        var packetsBeforeReturn = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(doorkeeper.InteractionId));

        var returnPackets = fixture.ReadPackets()
            .Skip(packetsBeforeReturn)
            .ToArray();
        Check.True(
            fixture.Store.PositionWrites is [_, var upper] &&
            upper.MapId == DuelArenaTransporterProtocol.MapId &&
            upper.X == DuelArenaTransporterProtocol.UpperArrivalX &&
            upper.Z == DuelArenaTransporterProtocol.UpperArrivalZ &&
            returnPackets.Count(static packet =>
                ReadOpcode(packet) == Opcodes.SceneChange) == 1 &&
            fixture.Character.PositionX ==
                DuelArenaTransporterProtocol.UpperArrivalX &&
            fixture.Character.PositionZ ==
                DuelArenaTransporterProtocol.UpperArrivalZ,
            "Arena_002 returns the same rehydrated actor from the lower " +
            "arena to the reviewed upper-lobby arrival");

        await CompleteRoundTripHandoffAsync(fixture, "upper lobby");
    }

    private static async Task CompleteRoundTripHandoffAsync(
        Fixture fixture,
        string destination)
    {
        await InvokeAsync(
            fixture.Handler,
            CreateControlPacket(Opcodes.ClientReady));
        await InvokeAsync(
            fixture.Handler,
            CreatePlayerDetailRequest());

        var catalog = GetHandlerField<
            Dictionary<uint, NpcSpawnDefinition>>(
            fixture.Handler,
            "_mapNpcsByInteractionId");
        Check.True(
            GetHandlerField<object>(
                fixture.Handler,
                "_pendingMapTransition") is null &&
            fixture.Registry.GetMapSessions(
                DuelArenaTransporterProtocol.MapId).Any(context =>
                    ReferenceEquals(context.Session, fixture.Session)) &&
            catalog is { Count: 2 } &&
            catalog.ContainsKey(
                DuelArenaTransporterProtocol.GatekeeperNpcId) &&
            catalog.ContainsKey(
                DuelArenaTransporterProtocol.DoorkeeperNpcId),
            $"the {destination} handoff reloads both reviewed Arena NPCs " +
            "before the next direction is available");
    }
}
