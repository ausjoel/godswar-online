using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

/// <summary>Which client item container a MSG_TIME_ITEM addresses.</summary>
internal enum TimeItemContainer
{
    Equipment = 0,
    KitBag = 1
}

internal static partial class PacketBuilder
{
    public const int TimeItemPacketLength = 52;

    // EXPIRY (b03). MSG_TIME_ITEM 10054, 52 bytes (client handler 0x4e0011,
    // Development/server-next/README.md):
    //   +4 i32 container (0 equipment, 1 kit bag)  +8 i32 page  +12 i32 index
    //   +16 struct tm, 9 x i32: sec, min, hour, mday, mon (0-11), year-1900,
    //       wday, yday (0-365), isdst
    // The client copies the tm into the item object (+0xbc) and the tooltip
    // counts down from it against its server clock (10311), which is realm
    // wall-clock time, so the tm is the realm's local time of the expiry.
    // An all-zero tm clears the slot's timer: the client never clears it
    // itself when another item later fills the slot.
    // Golden frame (gw-server-go capture): container 0, page 0, index 12,
    // tm {6,30,5,16,2,126,1,74,-1} = 2026-03-16 05:30:06.
    public static byte[] TimeItem(
        TimeItemContainer container,
        int page,
        int index,
        DateTime? realmWallClockExpiry)
    {
        var packet = new byte[TimeItemPacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), TimeItemPacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.TimeItem);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), (int)container);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), page);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), index);
        if (realmWallClockExpiry is { } at)
        {
            Span<int> tm =
            [
                at.Second,
                at.Minute,
                at.Hour,
                at.Day,
                at.Month - 1,
                at.Year - 1900,
                (int)at.DayOfWeek,
                at.DayOfYear - 1,
                -1
            ];
            for (var field = 0; field < tm.Length; field++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16 + (field * 4), 4), tm[field]);
            }
        }

        return packet;
    }

    /// <summary>Kit-bag slot to the client's (page, index) pair.</summary>
    public static (int Page, int Index) TimeItemKitBagAddress(int kitBagSlot) =>
        (kitBagSlot / KitBagSlotsPerPage, kitBagSlot % KitBagSlotsPerPage);
}
