using System.Buffers.Binary;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    internal static byte[] WonderlandPlayerDeath(uint playerObjectId)
    {
        // Installed Origin's MSG_DEAD (10027) opens the local revival dialog.
        // Its five optional reward recipients must be -1 so a player death
        // cannot overwrite experience or talent projections. Opcode 10018
        // loads a scene for the local object and must not be used here.
        var packet = new byte[116];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), MonsterDeathRewardOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), playerObjectId);
        for (var index = 0; index < 5; index++)
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8 + index * 4), -1);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(108), -1);
        return packet;
    }
}
