using System.Buffers.Binary;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // Actor attributes: captured 10356 types 15/49; native 0x4A8CDF also handles
    // type 51 as current talent EXP. Unlike 10027, none clears a monster's loot.
    public static IReadOnlyList<byte[]> WonderlandMonsterProgression(uint playerObjectId,
        long currentExperience, int currentTalentExperience, int currentTalentPoints)
    {
        var experience = WonderlandProgressionAttribute(playerObjectId, 15, 0);
        WriteLegacyFighterExperience(experience.AsSpan(12, 4), currentExperience, nameof(currentExperience));
        return [experience,
            WonderlandProgressionAttribute(playerObjectId, 51, Math.Max(0, currentTalentExperience)),
            WonderlandProgressionAttribute(playerObjectId, 49, Math.Max(0, currentTalentPoints))];
    }

    private static byte[] WonderlandProgressionAttribute(uint playerObjectId, uint type, int value)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 10356);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), playerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), value);
        return packet;
    }
}
