using System.Buffers.Binary;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public const int ForgeResultKindGemCompound = 2;
    public const int ForgeResultKindEquipmentCompound = 3;

    /// <summary>
    /// FORGEGEMS: S2C 10109 for the Gems (kind 2) and Comp (kind 3) tabs.
    /// Layout = 2008 MSG_EUQIPFORGE (Common/MsgDefine.h): head, bool Success
    /// (+4), int Type (+8), int BaseLv (+12), int AppLv (+16), int AppId[5]
    /// (+20..+39), 40 bytes. The 2.51 client (0x4e2967 -> 0x424530) prints
    /// CompoundSuccess/CompoundFailed for any kind other than 1 and then, for
    /// kind 2, adds the next-level gem locally (0x424b20) and, for kind 3,
    /// writes +12/+16 as quality/grade and +20.. as the five appended
    /// attribute ids of the main item (0x424cf0) WITHOUT checking +4. So a
    /// refusal must never use kind 2 or 3: use <see cref="ForgeResult"/> with
    /// kind 0. Kind 2 carries no item data (the 2008 server sent zeros).
    /// </summary>
    public static byte[] ForgeCompoundResult(int kind, CompactItemEntry result)
    {
        var packet = ForgeResult(success: true, resultKind: kind);
        if (kind == ForgeResultKindEquipmentCompound)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), result.Quality);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), result.Grade);
            WriteAttribute(packet, 20, result.Attribute1);
            WriteAttribute(packet, 24, result.Attribute2);
            WriteAttribute(packet, 28, result.Attribute3);
            WriteAttribute(packet, 32, result.Attribute4);
            WriteAttribute(packet, 36, result.Attribute5);
        }

        return packet;

        static void WriteAttribute(byte[] packet, int offset, int? attribute) =>
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset, 4), attribute ?? -1);
    }
}
