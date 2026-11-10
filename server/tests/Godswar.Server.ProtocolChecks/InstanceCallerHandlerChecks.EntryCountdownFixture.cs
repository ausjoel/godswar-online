using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static int? PendingEntrySceneId(GameClientHandler handler)
    {
        var pending = GetHandlerField<object>(handler, "_pendingInstanceEntry");
        return (int?)pending?.GetType().GetProperty("ClientSceneId")?.GetValue(pending);
    }

    private static async Task InvokeCountdownPacketAsync(GameClientHandler handler, GamePacket packet)
    {
        var gate = GetHandlerField<SemaphoreSlim>(handler, "_characterStateGate")!;
        await gate.WaitAsync();
        try
        {
            await (Task)(HandlePacketMethod.Invoke(handler, [packet, CancellationToken.None]) ??
                throw new InvalidOperationException("Instance Caller handler did not return a task."));
        }
        finally { gate.Release(); }
    }

    private static Task StopCountdownAsync(GameClientHandler handler) =>
        (Task)FindHandlerMethod("StopInstanceEntryCountdownAsync").Invoke(handler, null)!;

    private static async Task AwaitCountdownAsync(GameClientHandler handler) =>
        await GetHandlerField<Task>(handler, "_instanceEntryCountdownTask")!.WaitAsync(TimeSpan.FromSeconds(5));

    private static byte[][] ReadAdmissionPackets(IEnumerable<byte[]> emitted)
    {
        var packets = emitted.ToArray();
        if (packets.Length == 0 || ReadOpcode(packets[0]) != Opcodes.RepetitionQueueState)
            return packets;
        var sceneId = BinaryPrimitives.ReadInt32LittleEndian(packets[0].AsSpan(4));
        Check.True(packets.Length >= 2 && packets[0].SequenceEqual(EntryGolden(sceneId, 10222, 0)) &&
            packets[1].SequenceEqual(EntryGolden(sceneId, 10216, 0)),
            "admission starts with the complete native queue and zero-token Enter notices");
        var result = packets.Skip(2).ToList();
        // Rejection clears the native pending window as well as retaining the
        // existing dungeon-specific explanation. Never discard other traffic.
        var rejection = EntryGolden(sceneId, 10222, 2);
        var reset = Godswar.Server.Packets.PacketBuilder.RepetitionReset();
        var index = result.FindIndex(packet => packet.SequenceEqual(rejection) || packet.SequenceEqual(reset));
        if (index >= 0)
        {
            Check.True(index == 0 || index == result.Count - 1,
                "queue rejection brackets the admission explanation");
            result.RemoveAt(index);
        }
        return result.ToArray();
    }

    private static byte[] EntryGolden(int sceneId, ushort opcode, int state)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), sceneId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), state);
        return bytes;
    }
}
