using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    /// <summary>
    /// S2C 10112 (MSG_EQUIPREP) after a Level 3 Amethyst transform.
    /// </summary>
    /// <remarks>
    /// Origin.exe dispatch 0x4e2a70 passes object +0x10 to EquipForgeUI
    /// 0x5614c0; object offsets include the 4-byte transport prefix, so the new
    /// item id is packet +12. The length mirrors the 44-byte C2S frame; the
    /// other fields are not read by the client and stay zero.
    /// </remarks>
    public static byte[] AmethystTransformResult(uint newItemId)
    {
        const int packetLength = 44;
        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), packetLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.ForgeReplacementAction);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), newItemId);
        return packet;
    }
}
