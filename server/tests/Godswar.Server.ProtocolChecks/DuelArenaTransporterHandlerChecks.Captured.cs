using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class DuelArenaTransporterHandlerChecks
{
    private static async Task CheckCapturedTravelAsync()
    {
        var endpoints = new[]
        {
            (Key: "Arena_003", Id: 5199u, Dialog: 87,
                Script: "Arena_002", X: -5f, Z: 32f),
            (Key: "Arena_004", Id: 5201u, Dialog: 88,
                Script: "Arena_004", X: -104f, Z: 96f)
        };
        foreach (var endpoint in endpoints)
        {
            var spawn = NpcContentBaselineV7.LoadDefinitions()
                .Single(npc => npc.NpcKey == endpoint.Key);
            await using var fixture = await CreateFixtureAsync(
                new Endpoint(spawn, endpoint.X, endpoint.Z), captured: true);
            var action = CapturedArenaAction(endpoint.Id, endpoint.Dialog);

            await InvokeAsync(fixture.Handler, action);
            Check.Equal(0, fixture.Store.PositionWrites.Count,
                "captured initial travel requires an authorized NPC click");

            await InvokeAsync(fixture.Handler,
                CapturedArenaClick(endpoint.Id, length: 47));
            await InvokeAsync(fixture.Handler, action);
            Check.Equal(0, fixture.Store.PositionWrites.Count,
                "a short captured NPC click cannot issue travel authority");

            fixture.Character.PositionX = spawn.X + 13f;
            await InvokeAsync(fixture.Handler, CapturedArenaClick(endpoint.Id));
            await InvokeAsync(fixture.Handler, action);
            Check.Equal(0, fixture.Store.PositionWrites.Count,
                "captured travel rejects a distant NPC click");
            fixture.Character.PositionX = spawn.X;

            await InvokeAsync(fixture.Handler, CapturedArenaClick(endpoint.Id));
            var ack = fixture.ReadPackets().Last(packet =>
                ReadOpcode(packet) == Opcodes.NpcDialogOpen);
            Check.True(ack.Length == 48 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4)) == endpoint.Id &&
                BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(12)) == endpoint.Dialog &&
                Encoding.ASCII.GetString(ack.AsSpan(16)).TrimEnd('\0') == endpoint.Script,
                "NPC acknowledgement matches the captured actor, native dialog and script");

            var polluted = CapturedArenaAction(endpoint.Id, endpoint.Dialog);
            BinaryPrimitives.WriteInt32LittleEndian(polluted.Buffer.AsSpan(20), 0);
            await InvokeAsync(fixture.Handler, polluted);
            Check.Equal(0, fixture.Store.PositionWrites.Count,
                "captured travel rejects a changed meaningful argument");

            var before = fixture.ReadPackets().Count;
            await InvokeAsync(fixture.Handler, action);
            var emitted = fixture.ReadPackets().Skip(before).ToArray();
            Check.True(fixture.Store.PositionWrites is [var write] &&
                write.MapId == 57 &&
                (endpoint.Id == 5199u
                    ? DuelArenaEntryDestinations.All.Contains((write.X, write.Z))
                    : write.X == endpoint.X && write.Z == endpoint.Z) &&
                emitted.Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1 &&
                emitted.All(packet => ReadOpcode(packet) != Opcodes.NpcFunctionActionResponse),
                "initial travel persists a certified random entry or the fixed Ward return without a submenu");

            await InvokeAsync(fixture.Handler, action);
            Check.Equal(1, fixture.Store.PositionWrites.Count,
                "captured travel consumes the click context exactly once");
        }
    }

    private static GamePacket CapturedArenaClick(uint npcId, int length = 48)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.NpcDialogOpen);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), npcId);
        return new GamePacket(bytes);
    }

    private static GamePacket CapturedArenaAction(uint npcId, int dialogIndex)
    {
        var packet = CreateActionPacket(npcId, subId: -1,
            configureArguments: arguments =>
            {
                for (var index = 6; index < arguments.Length; index++)
                {
                    arguments[index] = 123_000 + index;
                }
            });
        BinaryPrimitives.WriteInt32LittleEndian(packet.Buffer.AsSpan(8), dialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(packet.Buffer.AsSpan(12), dialogIndex);
        return packet;
    }
}
