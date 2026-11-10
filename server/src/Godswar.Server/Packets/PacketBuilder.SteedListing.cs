using System.Buffers.Binary;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // ECONOMY r11 (2026-11-03): the Pet Merchant sells the Greek Steed (item 6000, the level 40 mount) again for
    // 15,000 silver, as the GodsArena wiki says (wiki-61-mount.txt: "a Greek Steed (Lvl 40) can be bought back from the
    // Pet Merchant NPC for 15,000 silver"). The listing is appended to the END of the first tab (category 0) of both
    // captured shelves (Athens PetMerchant and Sparta SpartaPetMerchant), so every captured listing keeps its
    // (category, index) and a purchase of an existing item resolves exactly as before.
    // Off switch: GODSWAR_PET_MERCHANT_STEED=0 (read once at start-up). An npc_shop_overrides list for the service still
    // replaces the whole shelf, as before (live has none for either pet merchant on 2026-11-03).
    internal const uint PetMerchantSteedItemId = 6000;
    internal const uint PetMerchantSteedPrice = 15_000;
    private const byte PetMerchantSteedCategory = 0;

    internal static bool PetMerchantSteedEnabled { get; } =
        Environment.GetEnvironmentVariable("GODSWAR_PET_MERCHANT_STEED") == "1"; // RELEASE: opt-in

    private static byte[] AppendPetMerchantSteed(byte[] catalog) =>
        PetMerchantSteedEnabled
            ? AppendSteedShelfListing(catalog, PetMerchantSteedCategory, PetMerchantSteedItemId, PetMerchantSteedPrice)
            : catalog;

    // Adds one plain listing after the last record of a category. The record is a copy of that frame's first record
    // (same quality/grade/bound/caption bytes as its neighbours) with the item id, the stack marker and the unit price
    // replaced and its attribute/socket area cleared. Frames already padded to sixteen records are written in place;
    // shorter frames grow by one record; a full frame gets a continuation frame (header byte 11 = 0).
    internal static byte[] AppendSteedShelfListing(byte[] catalog, byte category, uint itemId, uint price)
    {
        const int maximumRecords = 16;
        var lastFrame = -1;
        for (var offset = 0; offset < catalog.Length;
             offset += BinaryPrimitives.ReadUInt16LittleEndian(catalog.AsSpan(offset)))
        {
            if (catalog[offset + 8] == category && catalog[offset + 10] > 0)
            {
                lastFrame = offset;
            }
        }

        if (lastFrame < 0)
        {
            return catalog;
        }

        var frameLength = BinaryPrimitives.ReadUInt16LittleEndian(catalog.AsSpan(lastFrame));
        var count = catalog[lastFrame + 10];
        var record = catalog.AsSpan(lastFrame + ShopCatalogHeaderBytes, ShopCatalogItemBytes).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(record, itemId);
        record.AsSpan(4, 20).Fill(0xFF);
        record[27] = 1;
        record.AsSpan(28, 24).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(68), price);

        byte[] frame;
        if (count < maximumRecords)
        {
            var needed = ShopCatalogHeaderBytes + ((count + 1) * ShopCatalogItemBytes);
            frame = new byte[Math.Max(frameLength, needed)];
            catalog.AsSpan(lastFrame, frameLength).CopyTo(frame);
            BinaryPrimitives.WriteUInt16LittleEndian(frame, checked((ushort)frame.Length));
            record.CopyTo(frame, ShopCatalogHeaderBytes + (count * ShopCatalogItemBytes));
            frame[10] = checked((byte)(count + 1));
            var output = new byte[catalog.Length - frameLength + frame.Length];
            catalog.AsSpan(0, lastFrame).CopyTo(output);
            frame.CopyTo(output, lastFrame);
            catalog.AsSpan(lastFrame + frameLength).CopyTo(output.AsSpan(lastFrame + frame.Length));
            return output;
        }

        frame = new byte[ShopCatalogHeaderBytes + ShopCatalogItemBytes];
        catalog.AsSpan(lastFrame, ShopCatalogHeaderBytes).CopyTo(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame, checked((ushort)frame.Length));
        frame[10] = 1;
        frame[11] = 0;
        record.CopyTo(frame, ShopCatalogHeaderBytes);
        var insertAt = lastFrame + frameLength;
        var appended = new byte[catalog.Length + frame.Length];
        catalog.AsSpan(0, insertAt).CopyTo(appended);
        frame.CopyTo(appended, insertAt);
        catalog.AsSpan(insertAt).CopyTo(appended.AsSpan(insertAt + frame.Length));
        return appended;
    }
}
