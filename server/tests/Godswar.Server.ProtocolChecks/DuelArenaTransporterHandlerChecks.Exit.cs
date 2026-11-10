using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class DuelArenaTransporterHandlerChecks
{
    private static async Task CheckDoorkeeperExitAsync()
    {
        var spawn = NpcContentBaselineV7.LoadDefinitions()
            .Single(npc => npc.NpcKey == "Arena_002");
        foreach (byte camp in new byte[] { 0, 1 })
        {
            await using var fixture = await CreateFixtureAsync(
                new Endpoint(spawn, 20.0313f, -100.1129f), captured: true, camp: camp);
            var action = CapturedArenaAction(5197, 88);
            await InvokeAsync(fixture.Handler, action);
            await InvokeAsync(fixture.Handler, CapturedArenaClick(5197, length: 47));
            await InvokeAsync(fixture.Handler, action);
            Check.Equal(0, fixture.Store.PositionWrites.Count,
                "Doorkeeper exit requires a canonical authorized NPC click");

            await InvokeAsync(fixture.Handler, CapturedArenaClick(5197));
            await CheckDoorkeeperRejectedActionsAsync(fixture, spawn);
            var before = fixture.ReadPackets().Count;
            await InvokeAsync(fixture.Handler, action);
            var emitted = fixture.ReadPackets().Skip(before).ToArray();
            Check.True(fixture.Store.PositionWrites is [var write] &&
                write.MapId == camp && write.X == 20.031299591064453f &&
                write.Z == -100.11289978027344f &&
                fixture.Character.CurrentMap == camp &&
                emitted.Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1 &&
                emitted.Single(packet => ReadOpcode(packet) == Opcodes.SceneChange)
                    .SequenceEqual(PacketBuilder.SceneChange(LocalPlayerObjectId,
                        write.X, 0f, write.Z, camp)) &&
                emitted.All(packet => ReadOpcode(packet) != Opcodes.NpcFunctionActionResponse),
                "Doorkeeper persists the faction capital arrival and sends one direct scene change");
            await InvokeAsync(fixture.Handler, action);
            Check.Equal(1, fixture.Store.PositionWrites.Count,
                "Doorkeeper leave context is consumed once");

            await InvokeAsync(fixture.Handler, CreateControlPacket(Opcodes.ClientReady));
            await InvokeAsync(fixture.Handler, CreatePlayerDetailRequest());
            Check.True(GetHandlerField<object>(fixture.Handler, "_pendingMapTransition") is null &&
                fixture.Registry.GetMapSessions(camp).Any(context =>
                    ReferenceEquals(context.Session, fixture.Session)),
                "native readiness completes Arena-to-capital exit and reveals the player");
        }

        await using var failed = await CreateFixtureAsync(
            new Endpoint(spawn, 20.0313f, -100.1129f), captured: true);
        failed.Store.RejectPositionWrite = true;
        await InvokeAsync(failed.Handler, CapturedArenaClick(5197));
        await InvokeAsync(failed.Handler, CapturedArenaAction(5197, 88));
        Check.True(failed.Character.CurrentMap == 57 &&
            failed.Store.PositionWrites.Count == 0 &&
            failed.ReadPackets().All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "failed Doorkeeper persistence keeps the player in the Arena");
        failed.Store.RejectPositionWrite = false;
        await InvokeAsync(failed.Handler, CapturedArenaAction(5197, 88));
        Check.Equal(0, failed.Store.PositionWrites.Count,
            "a failed departure needs a new click before retrying");
        Check.True(!DuelArenaExitProtocol.TryResolveDestination(2, out _),
            "an unknown faction cannot select a capital exit");
    }

    private static async Task CheckDoorkeeperRejectedActionsAsync(Fixture fixture, NpcSpawnDefinition spawn)
    {
        var altered = CapturedArenaAction(5197, 88);
        BinaryPrimitives.WriteInt32LittleEndian(altered.Buffer.AsSpan(20), 0);
        await InvokeAsync(fixture.Handler, altered);
        var duplicateDialog = CapturedArenaAction(5197, 88);
        BinaryPrimitives.WriteInt32LittleEndian(duplicateDialog.Buffer.AsSpan(12), 87);
        await InvokeAsync(fixture.Handler, duplicateDialog);
        var shortBytes = CapturedArenaAction(5197, 88).Buffer[..88];
        BinaryPrimitives.WriteUInt16LittleEndian(shortBytes, 88);
        await InvokeAsync(fixture.Handler, new GamePacket(shortBytes));
        await InvokeAsync(fixture.Handler, CapturedArenaAction(5199, 88));
        fixture.Character.PositionX = spawn.X + 13;
        await InvokeAsync(fixture.Handler, CapturedArenaAction(5197, 88));
        fixture.Character.PositionX = spawn.X;
        fixture.Character.CurrentHp = 0;
        await InvokeAsync(fixture.Handler, CapturedArenaAction(5197, 88));
        fixture.Character.CurrentHp = 2000;
        Check.Equal(0, fixture.Store.PositionWrites.Count,
            "Doorkeeper rejects changed arguments, wrong dialogs/actors, short actions, distance and death");
    }
}
