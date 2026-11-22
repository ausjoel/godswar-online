using System.Buffers.Binary;
using Godswar.Server.Application.Awards;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    /// <summary>
    /// W4-B: the Prize Chest's award tab. GodsWar.exe 2.51 (handler of MSG_STORAGE 10034 at 0x4e0474, read
    /// 2026-11-12) sends a frame whose +18 storage type is not 0 to a second storage collection (object +0x142c8),
    /// the "AwardBags" tab of StorageUI.xml (40 cells, ids 300010-300049, a "B-Gold:" box and a "Draw" button).
    /// The layout is the normal warehouse frame: +4 money (shown as B-Gold), +8 owner, +12 capacity (the client
    /// keeps at most 200), +14 chunk selector (chunk * 2; chunk 0 clears the collection), +16 slot mask of the 12
    /// cells in this chunk, +18 type 1 (Award), then one 72-byte item record per set bit.
    /// SEEN IN THE REAL CLIENT 2026-11-12 (W5-F, client 1.4.9 on the private rig): the tab opens from the chest's
    /// "claim" line with the cells, stack counts and the B-Gold box filled; a later frame set repaints an open tab
    /// and does not open a closed one (screens in Development/w5/f-prize-chest/).
    /// </summary>
    public static byte[][] PrizeChestSnapshotPackets(IReadOnlyList<PrizeChestEntry> visibleItems, int bindingGold)
    {
        ArgumentNullException.ThrowIfNull(visibleItems);
        if (visibleItems.Count > PrizeChestPolicy.VisibleCells)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleItems));
        }

        const int capacity = PrizeChestPolicy.VisibleCells;
        var chunkCount = (capacity + WarehouseSnapshotSlotsPerChunk - 1) / WarehouseSnapshotSlotsPerChunk;
        var packets = new byte[chunkCount][];
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var firstSlot = chunk * WarehouseSnapshotSlotsPerChunk;
            ushort mask = 0;
            var records = new List<CompactItemEntry>(WarehouseSnapshotSlotsPerChunk);
            for (var bit = 0; bit < WarehouseSnapshotSlotsPerChunk && firstSlot + bit < capacity; bit++)
            {
                if (firstSlot + bit >= visibleItems.Count)
                {
                    break;
                }

                var entry = visibleItems[firstSlot + bit];
                mask |= checked((ushort)(1 << bit));
                records.Add(CompactItemEntry.Empty with
                {
                    Id = checked((uint)entry.ItemId),
                    Quality = entry.Quality,
                    Grade = entry.Grade,
                    Bound = entry.Bound ? (short)1 : (short)0,
                    Stack = (short)Math.Clamp(entry.Quantity, 1, LoginAwardPolicy.MaxItemQuantity)
                });
            }

            var packet = new byte[WarehouseSnapshotHeaderBytes + (records.Count * EnterItemRecordLength)];
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), checked((ushort)packet.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.WarehouseSnapshot);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), Math.Max(0, bindingGold));
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), LocalPlayerObjectId);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), capacity);
            packet[14] = checked((byte)(chunk * WarehouseSnapshotSelectorStride));
            packet[15] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(16, 2), mask);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(18, 2), (ushort)WarehouseStorageType.Award);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), 0);
            for (var record = 0; record < records.Count; record++)
            {
                WriteKitBagItemRecord(
                    packet.AsSpan(WarehouseSnapshotHeaderBytes + (record * EnterItemRecordLength), EnterItemRecordLength),
                    records[record]);
            }

            packets[chunk] = packet;
        }

        return packets;
    }

    /// <summary>
    /// W4-B: a 20-byte MSG_STORAGEITEM-style transfer (10059) aimed at the award storage: +18 = 1.
    /// </summary>
    public static bool IsPrizeChestTransfer(ReadOnlySpan<byte> frame) =>
        frame.Length == 20 &&
        BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]) == Opcodes.WarehouseTransfer &&
        BinaryPrimitives.ReadUInt16LittleEndian(frame[18..]) == (ushort)WarehouseStorageType.Award;

    /// <summary>
    /// W5-F: what an award-storage 10059 asks for. GodsWar.exe 2.51 (read 2026-11-12) has exactly four senders with
    /// +18 = 1, all with +16 (operation) = 0:
    ///   Draw button 300004 (0x621558): +4 = +6 = +8 = -1, +12 = -1                      -> everything;
    ///   right-click on an award cell (0x621adf): +4 = cell 0-39, +6 = +8 = -1, +12 = 0  -> that cell, any bag cell;
    ///   award cell dragged onto an empty bag cell (0x574d54 / 0x57bb8d): +4 = cell, +6 = bag page 0-3,
    ///     +8 = bag cell, +12 = 0                                                        -> that cell (the server
    ///     places it; the bag is repainted, the client keeps no lock on the target cell);
    ///   a NORMAL warehouse item dropped on an award cell (0x6219bf, the stock client's copy of the warehouse
    ///     move): +4 = warehouse slot, +6 = award cell, +8 = -1                           -> refused: nothing goes
    ///     INTO the chest from a player.
    /// Anything else is refused too.
    /// </summary>
    public static PrizeChestRequest ParsePrizeChestTransfer(ReadOnlySpan<byte> frame)
    {
        if (!IsPrizeChestTransfer(frame) || frame[16] != 0)
        {
            return PrizeChestRequest.Refused;
        }

        var slot = BinaryPrimitives.ReadInt16LittleEndian(frame[4..]);
        var page = BinaryPrimitives.ReadInt16LittleEndian(frame[6..]);
        var cell = BinaryPrimitives.ReadInt16LittleEndian(frame[8..]);
        var money = BinaryPrimitives.ReadInt32LittleEndian(frame[12..]);
        if (slot == -1 && page == -1 && cell == -1)
        {
            return money == -1 ? PrizeChestRequest.DrawAll : PrizeChestRequest.Refused;
        }

        if (money != 0 || slot < 0 || slot >= PrizeChestPolicy.VisibleCells)
        {
            return PrizeChestRequest.Refused;
        }

        var automatic = page == -1 && cell == -1;
        var toBagCell = page is >= 0 and <= 3 && cell >= 0;
        return automatic || toBagCell ? new PrizeChestRequest(PrizeChestRequestKind.Cell, slot) : PrizeChestRequest.Refused;
    }
}

internal enum PrizeChestRequestKind { Refused, DrawAll, Cell }

internal readonly record struct PrizeChestRequest(PrizeChestRequestKind Kind, int Cell)
{
    public static readonly PrizeChestRequest Refused = new(PrizeChestRequestKind.Refused, -1);
    public static readonly PrizeChestRequest DrawAll = new(PrizeChestRequestKind.DrawAll, -1);
}
