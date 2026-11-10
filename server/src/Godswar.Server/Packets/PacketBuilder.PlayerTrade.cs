using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

// TRADE: player trade window (TradeUI) and player stalls (StallSellUI / StallBuyUI).
// Layouts read from Origin.exe 2.51 (Q20 client). The client's handler pointer is the frame
// minus 4, so a handler read of [ebx+0x0c] is wire offset +8. All offsets below are WIRE
// offsets (0 = the u16 length). Source: Development/build/trade/README.md, "Protocol".
//
// Every frame that names a player is built PER RECIPIENT: the client finds the object with
// FindObjectByID (0x46b870) and string-compares the name with that object's name, dropping
// the frame on a mismatch. A client knows itself as LocalPlayerObjectId (0x1448) and everyone
// else by their registry object id.
internal static partial class PacketBuilder
{
    public const ushort PlayerTradeOpcode = 10104;          // 2008 MSG_TRADE (+27)
    public const ushort PlayerTradeMoneyOpcode = 10105;     // MSG_TRADE_MONEY
    public const ushort PlayerTradeItemOpcode = 10106;      // MSG_TRADE_ITEM, server to client only
    public const ushort PlayerTradeAddItemOpcode = 10107;   // MSG_TRADE_ADDITEM, client to server only
    public const ushort PlayerTradeRemoveItemOpcode = 10108; // MSG_TRADE_REMOVEITEM

    public const ushort PlayerStallOpcode = 10061;           // MSG_STALL
    public const ushort PlayerStallAddItemOpcode = 10062;    // MSG_STALLADDITEM
    public const ushort PlayerStallDeleteItemOpcode = 10063; // MSG_STALLDELITEM
    public const ushort PlayerStallItemOpcode = 10065;       // MSG_STALLITEM
    public const ushort PlayerStallBuyItemOpcode = 10066;    // MSG_STALLBUYITEM

    public const int PlayerTradeLength = 148;
    public const int PlayerTradeMoneyLength = 80;
    public const int PlayerTradeAddItemLength = 84;
    public const int PlayerTradeRemoveItemLength = 76;
    // UNPROVEN length: the 10106 handler (0x4e27ec) reads +4 id, +8 slot, +12 base id (log only),
    // a NUL-terminated name from +16, and a 72-byte item record from +48. Nothing past +120 is read.
    public const int PlayerTradeItemLength = 48 + EnterItemRecordLength;
    public const int PlayerTradeNameLength = 64;
    public const int PlayerTradeItemNameLength = 32;

    public const int PlayerStallLength = 108;
    public const int PlayerStallAddItemLength = 40;
    public const int PlayerStallDeleteItemLength = 12;
    public const int PlayerStallItemLength = 136;
    public const int PlayerStallBuyItemLength = 104;
    public const int PlayerStallTextChars = 32;

    // MSG_TRADE State values (2008 Trade.h enum TradeState, unchanged in the 2.51 handler 0x4e24c8).
    public const int PlayerTradeStateIdle = 0;
    public const int PlayerTradeStateStart = 1;   // client: open the window
    public const int PlayerTradeStateLock = 2;    // client: redraw locks
    public const int PlayerTradeStateComplete = 3; // client: close the window
    public const int PlayerTradeStateCancel = 4;   // client: close the window

    public static uint TradeObjectIdFor(uint subjectObjectId, bool subjectIsRecipient) =>
        subjectIsRecipient ? LocalPlayerObjectId : subjectObjectId;

    /// <summary>
    /// 10104, 148 bytes: +4 uiID, +8 uiObjectID, +12 State, +16 TraderState, +20 chName[64],
    /// +84 chObjectName[64]. The handler sets uiID's trade state to State and uiObjectID's to
    /// TraderState; State 1 opens TradeUI, 3 and 4 close it.
    /// </summary>
    public static byte[] PlayerTrade(
        uint objectId,
        string name,
        uint partnerObjectId,
        string partnerName,
        int state,
        int partnerState)
    {
        var packet = NewFrame(PlayerTradeLength, PlayerTradeOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), partnerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), state);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), partnerState);
        PacketText.WriteFixedAscii(packet.AsSpan(20, PlayerTradeNameLength - 1), name);
        PacketText.WriteFixedAscii(packet.AsSpan(84, PlayerTradeNameLength - 1), partnerName);
        return packet;
    }

    /// <summary>10105, 80 bytes: +4 uiID (the offering player), +8 silver, +12 gold, +16 name[64].</summary>
    public static byte[] PlayerTradeMoney(uint objectId, string name, int silver, int gold)
    {
        var packet = NewFrame(PlayerTradeMoneyLength, PlayerTradeMoneyOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), silver);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), gold);
        PacketText.WriteFixedAscii(packet.AsSpan(16, PlayerTradeNameLength - 1), name);
        return packet;
    }

    /// <summary>
    /// 10106 (UNPROVEN length and name width, see <see cref="PlayerTradeItemLength"/>): +4 uiID
    /// (the offering player), +8 trade slot 0..11, +12 base item id, +16 name, +48 item record.
    /// </summary>
    public static byte[] PlayerTradeItem(uint objectId, string name, int tradeSlot, CompactItemEntry item)
    {
        if (tradeSlot is < 0 or >= 12) throw new ArgumentOutOfRangeException(nameof(tradeSlot));
        if (item.IsEmpty) throw new ArgumentException("A trade slot needs a real item.", nameof(item));
        var packet = NewFrame(PlayerTradeItemLength, PlayerTradeItemOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), tradeSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), item.Id);
        PacketText.WriteFixedAscii(packet.AsSpan(16, PlayerTradeItemNameLength - 1), name);
        WriteKitBagItemRecord(packet.AsSpan(48, EnterItemRecordLength), item);
        return packet;
    }

    /// <summary>10108, 76 bytes: +4 uiID (the offering player), +8 trade slot, +12 name[64].</summary>
    public static byte[] PlayerTradeRemoveItem(uint objectId, string name, int tradeSlot)
    {
        var packet = NewFrame(PlayerTradeRemoveItemLength, PlayerTradeRemoveItemOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), tradeSlot);
        PacketText.WriteFixedAscii(packet.AsSpan(12, PlayerTradeNameLength - 1), name);
        return packet;
    }

    /// <summary>
    /// 10061, 108 bytes (the captured Origin server sent 106; the handler reads nothing past +74):
    /// +4 uiID, +8 u8 State (1 open, 0 closed), +10 wchar Text[32], +74 char SellerName[32].
    /// The handler stores State at player+0x23354, which CheckStall (0x47e1a7) requires before it
    /// sends 10065, and closes StallSellUI / StallBuyUI on State 0.
    /// </summary>
    public static byte[] PlayerStall(uint objectId, bool open, string text, string sellerName)
    {
        var packet = NewFrame(PlayerStallLength, PlayerStallOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        packet[8] = open ? (byte)1 : (byte)0;
        var chars = (text ?? string.Empty).AsSpan();
        for (var i = 0; i < Math.Min(chars.Length, PlayerStallTextChars - 1); i++)
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10 + i * 2, 2), chars[i]);
        PacketText.WriteFixedAscii(packet.AsSpan(74, 31), sellerName);
        return packet;
    }

    /// <summary>
    /// 10062 echo to the seller, 40 bytes: +4 stall index 0..15, +8 pocket 0..2, +12 bag page,
    /// +16 bag grid, +20 silver per unit, +24 gold per unit, +32 int64 stall goods id. The handler
    /// (0x4e09ca -> 0x4bfff0) puts the bag item into the local stall only when the client locked
    /// that bag item for the stall first (item state 6).
    /// </summary>
    public static byte[] PlayerStallAddItem(
        int stallIndex, int pocket, int bagPage, int bagGrid, int silver, int gold, long goodsId)
    {
        var packet = NewFrame(PlayerStallAddItemLength, PlayerStallAddItemOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), stallIndex);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), pocket);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), bagPage);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), bagGrid);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), silver);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24, 4), gold);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(32, 8), goodsId);
        return packet;
    }

    /// <summary>10063, 12 bytes: +4 stall index, +8 pocket. The handler only redraws StallSellUI.</summary>
    public static byte[] PlayerStallDeleteItem(int stallIndex, int pocket)
    {
        var packet = NewFrame(PlayerStallDeleteItemLength, PlayerStallDeleteItemOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), stallIndex);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), pocket);
        return packet;
    }

    /// <summary>
    /// 10065, 136 bytes, one per item on show: +8 int64 goods id, +16 seller name (UNPROVEN: the
    /// handler does not read it; it is where the client's own request carries the name), +48
    /// seller uiID, +52 u8 pocket, +53 u8 index, +56 silver per unit, +60 gold per unit, +64 item
    /// record. Handler 0x4e08dd -> 0x4c0a30; it refuses an occupied stall slot, and the client
    /// clears the seller's stall (0x4bff00) before it asks.
    /// </summary>
    public static byte[] PlayerStallItem(
        uint sellerObjectId, string sellerName, long goodsId, int pocket, int stallIndex,
        int silver, int gold, CompactItemEntry item)
    {
        if (pocket is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(pocket));
        if (stallIndex is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(stallIndex));
        if (item.IsEmpty) throw new ArgumentException("A stall slot needs a real item.", nameof(item));
        var packet = NewFrame(PlayerStallItemLength, PlayerStallItemOpcode);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(8, 8), goodsId);
        PacketText.WriteFixedAscii(packet.AsSpan(16, 31), sellerName);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(48, 4), sellerObjectId);
        packet[52] = (byte)pocket;
        packet[53] = (byte)stallIndex;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(56, 4), silver);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(60, 4), gold);
        WriteKitBagItemRecord(packet.AsSpan(64, EnterItemRecordLength), item);
        return packet;
    }

    /// <summary>
    /// 10066, 104 bytes: +4 seller uiID, +8 buyer uiID, +12 seller name[32], +44 buyer name[32],
    /// +80 int64 goods id, +88 pocket, +92 index, +96 quantity. Sent to buyer and seller after the
    /// purchase commits; both handlers (0x4c0e30 buyer side, 0x4c0ad0 seller side) take the
    /// quantity off the stall slot.
    /// </summary>
    public static byte[] PlayerStallBuyItem(
        uint sellerObjectId, string sellerName, uint buyerObjectId, string buyerName,
        long goodsId, int pocket, int stallIndex, int quantity)
    {
        var packet = NewFrame(PlayerStallBuyItemLength, PlayerStallBuyItemOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), sellerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), buyerObjectId);
        PacketText.WriteFixedAscii(packet.AsSpan(12, 31), sellerName);
        PacketText.WriteFixedAscii(packet.AsSpan(44, 31), buyerName);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(80, 8), goodsId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(88, 4), pocket);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(92, 4), stallIndex);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(96, 4), quantity);
        return packet;
    }

    private static byte[] NewFrame(int length, ushort opcode)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), opcode);
        return packet;
    }
}
