using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // Native10050 is asymmetric: the client sends20 bytes; the server ACK is16.
    // A zero player ID deliberately selects the native loot-only observer path:
    // it clears a known occupied slot/UI without adding anything to the bag.
    public static byte[] WonderlandBossLootPickupAck(uint playerObjectId, uint monsterObjectId, int pickupIndex)
    {
        if (monsterObjectId == 0 || pickupIndex is < 0 or >= 8)
            throw new ArgumentOutOfRangeException(nameof(pickupIndex));
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), Opcodes.MoveItem);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), playerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), monsterObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), pickupIndex);
        return packet;
    }
}
