using System.Buffers.Binary;

namespace Godswar.Server.Packets;

// PROF slice 2 (2026-11-09): the client's gathering messages, read from the 2013 Origin.exe (= 1.4.4 GodsWar.exe).
//  - CCollectionSKillUI builds C2S 10344 at 0x658508 (start, also every 2 s while gathering, 0x6589f0) and 0x658767
//    (stop), and C2S 10342 beside each (0x65854c / 0x6587b4 / 0x6590dc on close).
//  - S2C 10342 is handled at 0x4e094b (the handler logs "MSG_DELETE_ITEM", a copy-paste in the client): it plays
//    (+4 = 1) or stops (+4 = 0) another player's gather rabbit.
//  - S2C 10345 is handled at 0x658b70 (it logs "_MSG_SYN_AP", also a copy-paste) through result +4.
// Full table, addresses and confidence: docs/professions/README.md section 6.
internal static partial class PacketBuilder
{
    public const ushort GatherPoseOpcode = 10342;    // C2S 16 / S2C 16 (relayed)
    public const ushort GatherRequestOpcode = 10344; // C2S 16
    public const ushort GatherResultOpcode = 10345;  // S2C 80

    public const int GatherFrameLength = 16;
    public const int GatherResultLength = 80;

    /// <summary>10345 results (handler 0x658b70).</summary>
    public const int GatherObtained = 0;     // "You have obtained: <name> <count>", the item is put in the bag (0x4ac810)
    public const int GatherWrongTool = 1;    // ERROR_0600 + Collection_Msg7, stops
    public const int GatherNothing = 2;      // no branch: nothing shown, gathering goes on
    public const int GatherBagFull = 3;      // ERROR_0602 + Collection_Msg6, stops
    public const int GatherNoAp = 4;         // ERROR_0603 + Collection_Msg8, stops
    public const int GatherError = 5;        // ERROR_0604 + Collection_Msg9, stops
    public const int GatherIllegal = 6;      // Collection_Msg10 + ERROR_0605, stops
    public const int GatherCloseWindow = -1; // closes the window (0x6590d0)

    /// <summary>A C2S 10344 (+4 the tool word the window holds, +8 CollNpc id, +12 u8 1 = gather / 0 = stop).</summary>
    public readonly record struct GatherRequest(int ToolWord, int NodeId, bool Gathering);

    /// <summary>A 10342 (+4 1 = rabbit on / 0 = off, +8 CollNpc id, +12 player object id).</summary>
    public readonly record struct GatherPose(int State, int NodeId, uint ObjectId);

    public static bool TryReadGatherRequest(ReadOnlySpan<byte> buffer, out GatherRequest request)
    {
        request = default;
        if (buffer.Length < GatherFrameLength)
        {
            return false;
        }

        request = new GatherRequest(
            BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4)),
            buffer[12] != 0);
        return true;
    }

    public static bool TryReadGatherPose(ReadOnlySpan<byte> buffer, out GatherPose pose)
    {
        pose = default;
        if (buffer.Length < GatherFrameLength)
        {
            return false;
        }

        pose = new GatherPose(
            BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12, 4)));
        return true;
    }

    /// <summary>S2C 10342 for the other players on the map: the same layout, the sender's object id at +12.</summary>
    public static byte[] GatherPoseFrame(int state, int nodeId, uint objectId)
    {
        var packet = new byte[GatherFrameLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, GatherFrameLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), GatherPoseOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), state);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), nodeId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), objectId);
        return packet;
    }

    /// <summary>
    /// S2C 10345: +4 result, +8 the 72-byte kit-bag item record of what was gathered (read only for result 0:
    /// 0x438050 on the id, the client puts the item in its first empty bag slot, and prints record[27] as the count).
    /// </summary>
    public static byte[] GatherResult(int result, uint itemId = 0, int count = 0)
    {
        var packet = new byte[GatherResultLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, GatherResultLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), GatherResultOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), result);
        if (result == GatherObtained && itemId != 0)
        {
            // The kit-bag record shape (PacketBuilder.ItemSerialization): id, five empty attributes,
            // quality/grade 1, unbound, stack (never above 99).
            var record = packet.AsSpan(8, 72);
            BinaryPrimitives.WriteUInt32LittleEndian(record, itemId);
            for (var offset = 4; offset <= 20; offset += 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(offset, 4), -1);
            }

            record[24] = 1;
            record[25] = 1;
            record[26] = 0;
            record[27] = (byte)Math.Clamp(count, 1, 99);
        }

        return packet;
    }
}
