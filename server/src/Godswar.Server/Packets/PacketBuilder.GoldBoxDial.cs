using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

// The box dial (CDialUI). Layouts read from Origin.exe 2.51: the 10121 handler is at 0x4df02e and calls
// 0x556830, the 10120 handler is at 0x4defbf. Wire offsets below; the client's buffer is offset by 4.
internal static partial class PacketBuilder
{
    public const ushort GoldBoxRequestOpcode = 10118;
    public const ushort GoldBoxReturnOpcode = 10120;
    public const ushort GoldBoxInDialOpcode = 10121;

    // 16 ring items and the centre item. Each record goes through 0x438050/0x441ea0, the same converter
    // as SystemAddItem, so it uses the kit bag record.
    public const int GoldBoxDialItemCount = 17;
    public const int GoldBoxInDialLength = 16 + GoldBoxDialItemCount * EnterItemRecordLength;

    // 2008 MSG_GOLD_BOX_RETURN: head, ucInfo, UINT DialItem[17].
    public const int GoldBoxReturnLength = 76;

    // ucInfo values. 3 starts the spin towards a ring index, 7 puts that ring item in the reward slot,
    // 9 makes the client resend its last claim. 5 (centre item) is never sent: before an InDial it
    // indexes the bag with slot 0xFF.
    public const byte GoldBoxReturnSpin = 3;
    public const byte GoldBoxReturnClaimed = 7;

    /// <summary>
    /// +4 reward slot (the client parks a locked placeholder there), +8 extra-draw item id (0 hides
    /// "Extra draw"), +12 unused, +16 seventeen 72-byte item records.
    /// </summary>
    public static byte[] GoldBoxInDial(int rewardSlot, IReadOnlyList<CompactItemEntry> items)
    {
        if (rewardSlot is < 0 or >= 96) throw new ArgumentOutOfRangeException(nameof(rewardSlot));
        if (items.Count != GoldBoxDialItemCount || items.Any(item => item.IsEmpty))
            throw new ArgumentException("The dial needs seventeen real items.", nameof(items));
        var packet = new byte[GoldBoxInDialLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), GoldBoxInDialOpcode);
        packet[4] = (byte)rewardSlot;
        for (var i = 0; i < items.Count; i++)
            WriteKitBagItemRecord(packet.AsSpan(16 + i * EnterItemRecordLength, EnterItemRecordLength), items[i]);
        return packet;
    }

    /// <summary>+4 ucInfo, +8 ring index (0-15), +12 reward slot.</summary>
    public static byte[] GoldBoxReturn(byte info, int ringIndex, int rewardSlot)
    {
        if (info is not (GoldBoxReturnSpin or GoldBoxReturnClaimed)) throw new ArgumentOutOfRangeException(nameof(info));
        if (ringIndex is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(ringIndex));
        if (rewardSlot is < 0 or >= 96) throw new ArgumentOutOfRangeException(nameof(rewardSlot));
        var packet = new byte[GoldBoxReturnLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), GoldBoxReturnOpcode);
        packet[4] = info;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), (uint)ringIndex);
        packet[12] = (byte)rewardSlot;
        return packet;
    }
}
