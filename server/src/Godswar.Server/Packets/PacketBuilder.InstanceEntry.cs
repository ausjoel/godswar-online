using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // 10222 controls the queue icon/result; 10216 opens the native Enter
    // window, whose client-owned display timer starts at sixty seconds.
    public static byte[] InstanceEntryQueueState(int clientSceneId, int state = 0)
    {
        if (state is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(state));
        return InstanceEntryControl(Opcodes.RepetitionQueueState, clientSceneId, state);
    }

    public static byte[] InstanceEntryNotice(int clientSceneId) =>
        InstanceEntryControl(Opcodes.RepetitionNotice, clientSceneId, 0);

    private static byte[] InstanceEntryControl(ushort opcode, int clientSceneId, int value)
    {
        if (clientSceneId <= 0)
            throw new ArgumentOutOfRangeException(nameof(clientSceneId));
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), clientSceneId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), value);
        return packet;
    }
}
