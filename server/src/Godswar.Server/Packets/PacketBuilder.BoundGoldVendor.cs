using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // Preserve captured stock apart from this vendor's tab-caption metadata.
    // This revision never edits the separate B-GOLD Shop.
    private static byte[] ReviseBoundGoldVendorCatalog(byte[] captured)
    {
        var categories = Enumerable.Range(0, 4)
            .Select(_ => new List<byte[]>()).ToArray();
        var offset = 0;
        while (offset < captured.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                captured.AsSpan(offset));
            var category = captured[offset + 8];
            for (var index = 0; index < captured[offset + 10]; index++)
            {
                var record = captured.AsSpan(
                    offset + ShopCatalogHeaderBytes + index * ShopCatalogItemBytes,
                    ShopCatalogItemBytes).ToArray();
                var id = BinaryPrimitives.ReadUInt32LittleEndian(record);
                if (!RemoveBoundGoldVendorItem(category, id))
                {
                    categories[category].Add(record);
                }
            }
            offset += length;
        }

        // Leo is the stock level-10 ring. Sell its normal base definition,
        // without copying the captured amulet's appended attributes.
        categories[0].Add(NewBoundGoldVendorItem(3200, 5_000));
        categories[1].Add(NewBoundGoldVendorItem(11005, 5_000));
        // Use the existing Holy Stone / elemental spirit unit-price families.
        categories[2].Add(NewBoundGoldVendorItem(9032, 460));
        foreach (var id in new uint[] { 9090, 9091, 9092, 9093 })
        {
            categories[2].Add(NewBoundGoldVendorItem(id, 230));
        }
        // Keep the three Holy Stone elements together at the start of H.Ward.
        var holyStones = new uint[] { 9030, 9031, 9032 }.Select(id =>
            categories[2].Single(record =>
                BinaryPrimitives.ReadUInt32LittleEndian(record) == id)).ToArray();
        categories[2].RemoveAll(record => holyStones.Contains(record));
        categories[2].InsertRange(0, holyStones);
        // Socket Spells finish H.Ward in tier order. Keep captured I/II
        // records and prices; III/IV use the approved new B-Gold prices.
        var socketSpells = new uint[] { 4270, 4271 }.Select(id =>
            categories[2].Single(record => BinaryPrimitives.ReadUInt32LittleEndian(record) == id)).ToArray();
        categories[2].RemoveAll(record => socketSpells.Contains(record));
        categories[2].AddRange(socketSpells);
        categories[2].Add(NewBoundGoldVendorItem(4272, 392_000));
        categories[2].Add(NewBoundGoldVendorItem(4273, 1_568_000));
        CompleteBoundGoldHolySuitWareListings(categories[3]);
        // Ascension Core follows Holy Box V at the approved per-unit B-Gold price.
        categories[3].Add(NewBoundGoldVendorItem(9025, 50_000));

        using var output = new MemoryStream();
        for (var category = 0; category < categories.Length; category++)
        {
            var records = categories[category];
            // Native CNPC imports record byte 64 as its caption key, and
            // NPCTrade resolves the first item's key in EquipDescription.dat.
            // 44/45 are vendor-owned client labels; 26/30 retain Pet/H.Ward.
            var captionKey = category switch
            {
                0 => (byte)44, 1 => (byte)26, 2 => (byte)30, _ => (byte)45
            };
            foreach (var record in records)
            {
                record[64] = captionKey;
            }
            for (var first = 0; first < records.Count; first += 16)
            {
                var count = Math.Min(16, records.Count - first);
                var frame = new byte[ShopCatalogHeaderBytes +
                    count * ShopCatalogItemBytes];
                BinaryPrimitives.WriteUInt16LittleEndian(frame,
                    checked((ushort)frame.Length));
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2),
                    Opcodes.NpcShopCatalog);
                frame[8] = checked((byte)category);
                frame[9] = 4; // Native Binding Gold wallet and display.
                frame[10] = checked((byte)count);
                frame[11] = first == 0 ? (byte)1 : (byte)0;
                for (var index = 0; index < count; index++)
                {
                    records[first + index].CopyTo(frame,
                        ShopCatalogHeaderBytes + index * ShopCatalogItemBytes);
                }
                output.Write(frame);
            }
        }
        return output.ToArray();
    }

    private static void CompleteBoundGoldHolySuitWareListings(List<byte[]> records)
    {
        // Captured wares have separate single and 25-unit quantity presets.
        // Complete both sequences without replacing their unit-price records.
        var singles = new uint[] { 9010, 9011, 9012, 9013 }.Select(id =>
        {
            var record = records.Single(value => BinaryPrimitives.ReadUInt32LittleEndian(value) == id).ToArray();
            record[27] = 1;
            return record;
        }).ToArray();
        records.InsertRange(0, singles);
        foreach (var marker in new byte[] { 1, 25 })
        {
            var previous = records.FindIndex(value =>
                BinaryPrimitives.ReadUInt32LittleEndian(value) == 9016 && value[27] == marker);
            var divinium = NewBoundGoldVendorItem(9017, 31_346);
            divinium[27] = marker;
            records.Insert(previous + 1, divinium);
        }
    }

    private static bool RemoveBoundGoldVendorItem(int category, uint id) =>
        category switch
        {
            2 => id is 4011 or 4262 or 4263 or 4264 or 4265 or
                4302 or 4306 or 4324 or 4325 or 4326 or 4327,
            3 => id is 4010 or 4041 or >= 9060 and <= 9067 or
                >= 9080 and <= 9087,
            _ => false
        };

    private static byte[] NewBoundGoldVendorItem(uint id, int unitPrice)
    {
        var record = new byte[ShopCatalogItemBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(record, id);
        for (var offset = 4; offset <= 20; offset += 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(offset), -1);
        }
        record[24] = 1;
        record[25] = 1;
        record[26] = 1;
        record[27] = 1;
        record[65] = 1;
        record[66] = 0xff;
        record[67] = 0xff;
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(68), unitPrice);
        record[80] = 1;
        return record;
    }
}
