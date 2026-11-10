using System.Buffers.Binary;
using System.IO.Compression;
using Godswar.Server.Application.Npc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int ShopCatalogHeaderBytes = 16;
    private const int ShopCatalogItemBytes = 88;

    // These captured records have no ItemBaseAttribute entry in the local
    // client. Advertising them makes Origin dereference a missing definition
    // while constructing the vendor window.
    private static readonly uint[] UnsupportedBoundGoldVendorItemIds =
        [4064, 4177, 4515, 10059];

    // Exact opcode-10071 catalog streams captured from the reference server on
    // 2026-08-28. Only the authoritative NPC ID and live B-Gold balance are
    // patched at egress; every item record, category, order, and price remains
    // byte-identical to the capture.
    private const string BoundGoldVendorCatalogGzip =
        "H4sIAAAAAAAEAO3bz2sTQRQH8EnS2Ji0Nm2RUsFALZiKnqLWqhV7qgc9pIqKtQrSatIKXuqPtGoVQfEi9qpehEL9BXqwF/8EQUR68qDiQSsUT+JJlLib5DljdjJMeO9dyjwM6UL2k3E6+3U2L85Gj6YPrxVChJOhh7dCYjkihPdHZL1Hj/coVirklbCpULGYTCuHlefpqHT7Cd35VUJ0eM/D3mOB0I03SpdyvL0xIZq957z3mCN0F73xdomyfY/QfRAru9Tj/RMrrwd/jVHO76nV5d8btbspLkROlOe3i9C947mDDO6PuJzfLLEL8ztM6B5JCJER5bnIEbqvEnJuKd31TXKsBUJ3VuaxUPP4pvg/kyMRYfU+bZFi8XZ78H0gj8GFawXrQh77rprJWBfyGFyq8UIe+66acVgX8th31UzGupDH1OOFPPZdNTuxLuQxtQt5XJ2dWLc6j+8TuZDH/t9fzWQKt0NxIZOxrprH/u8OshPr+nkM14SayVhXzeO7QmYy1u2r5HEo3FLaHzcnZdarZZv767zc/x0Lvs9EGu8mlGM4qZVgvCmN28Y03nYCVzfejUzjTTONdxfTeHczjfcAkztJ4A4or4QfRwncG5rxnidwD2ncJMF1vKRxLzKts1mZm6V97AWC9zmneZ9pAlc3L5eYxlsgcNdo1vMU03g/tODdjMb9SOCOa9xPBG6d62HU1q1zfq3dOufX2q1zfq1d3fwa9inWru7fD0POW7u6nI9uxq+z75r9JldBHofD8VIeD3bjxp/yxv9Gk0P7mNwsgTupHMNJQwSubj0fJHBfhoPuMJN7nMBdiAbdEQI3r2ww4KQTBG5/T9A9SeCO9QddV67Uqs7jMaYcOs3knmFyc0xunskdZ3InmNyzTG6ByZ1icl25UkvmcayUx9NM6+4yk3uFyb3K5M4wudeY3OfIz9989/2xoPuCwH03I1/q8s3VSqjq/fEXguukWzmGk74yuUtM7jcmd4jAVV8IPz9Dfl6YqvG53jzBeHU5/4jJfczkPmFyXblSS+ZxuJTHbwnWXaNyDCctMrmuXLlytVIK8jhS2R/3Etz3ZpvkMZy0g8D9tSHo9hG4O/cE3ZGa+0377wHq9kNzrXj3p6avaejnddq4tfp5hn6CtVtnPwHlGvoJKNfQT0C5rlypVZ3Hhn4Tat0Z+k0o19BvQrmGfhPKNfSbUK6h34RyDf0ElGvoJ6BcQz8B5Rr6CS6PXZFVdR4b+k2odWfoN6Hcz8jvsaZq/D+T/cjvCfvuFs0+NkMwDx3KMbhbCVzd9/62Ebh7Nfvu7QTu9Yaga7i/s3brvL+zdnX3d65cqSXzuKGUx4b7f+t1p7v/f1o7N61d5TL5574mcHV9rAGCz0E6NTnvypUrV7XqL5yJVYP4RwAA";

    private const string BindingGoldShopCatalogGzip =
        "H4sIAAAAAAAEAO3Zz0tUURTA8ffG0VGwZiSdmugXSElBG7MCy0aIWrQICiOicmMTKdgipU0/cDVlKzeVREaLaJEQkQT9CWlaWkGRVosg+oGbaOEimHzD3ObBO8nIPV8heGc1F+Z+3uHMfYcz7w2UH21oq3McJ5pw72ZdJ5twnJwQ7nw4JcQqN5er9q3NpisK7gnBvargDvm+aT72K7iDZUF3gfqmSnUXWd+S3UXWt2RXqu8ZhfpuFvLtVHC7BLdLwU1Egu45qA49UB16oTpchOpwCarDZagOA3/7cdzx+vHYJvvrtAjXeQ6545A7AbkvIPcl5E5C7hTkvoLcrwpuj29tNn1TcJPRoPtdwZ2tCboVDfZuujLo3lbob/VCHWIK+f4S5gkqTD92C/Px4xq7/CNeXXxrs2lEwd0vuDcV3JnyoPtEwb0l/I4P4vauNB8PK7jrBPcZlO8olO8Nhd9tn+BOWvYLz40J7hTkvofcacidgdxPCm5ccMehfIv9uCo/H09A13kLue8g9wPkfoTOR2O9vbtScLcpuNJc2KTgpoU5druC2yfMWWGEsRRR7MexfD/erXCeq3xrs6lFwd0puHsU3C+Cm1ZwR4R+8cPyf6TnDjYH3bkt9u5BId8F3A4bN+zHYYQRDNOPI4XnFa3r7c5zcv487/KtzaZ7Cu604B6A8n0I5ZuF8n0K5Tuq4B4T3DEFd5nwPOg1lO8bKN82KN/jUL7tUL4ZKN9uy+dintso5Htawe0U3GI/rsjPx2cVriPNmx1QXU5BdclAdVizwd79LbwPaYbcMMIIY+nC9OOywnzcv8Luvk79476+DrlDkPsIcg/VMu5JyM1AbjfkpuoYdyPkboXcVsidg9xoknGrIfczVIdiP16en4+vKfShPt8LdbPpDuTeh9xhyD2s0Ick9wjU385D+V6A3NUK94nkroXuvx2QuxdyZ6H6/oTc/y3+ANMqFmwQMgAA";

    private static readonly Lazy<byte[]> BoundGoldVendorCatalog = new(
        () => FilterUnsupportedCatalogItems(
            InflateCatalog(
                BoundGoldVendorCatalogGzip,
                expectedLength: 18_424,
                expectedPacketCount: 13,
                expectedCapturedNpcId: 5_461),
            UnsupportedBoundGoldVendorItemIds));

    private static readonly Lazy<byte[]> BindingGoldShopCatalog = new(
        () => InflateCatalog(
            BindingGoldShopCatalogGzip,
            expectedLength: 12_816,
            expectedPacketCount: 9,
            expectedCapturedNpcId: 5_460));

    public static byte[] CapitalNpcShopCatalog(
        uint npcId,
        CapitalNpcShopBalances balances,
        CapitalNpcServiceKind service)
    {
        var source = GetCapitalShopCatalogSource(service);

        var packets = (byte[])source.Clone();
        var offset = 0;
        while (offset < packets.Length)
        {
            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(offset, sizeof(ushort)));
            if (packetLength < ShopCatalogHeaderBytes ||
                offset + packetLength > packets.Length)
            {
                throw new InvalidDataException(
                    "Captured capital shop catalog has an invalid frame.");
            }

            // Frame balance. The Point Exchanger charges three balances but can
            // advertise only one frame-level code, so the balance advertised for
            // a frame is the one that frame's own listings charge.
            var frameItemId = packets[offset + 10] == 0
                ? 0u
                : BinaryPrimitives.ReadUInt32LittleEndian(
                    packets.AsSpan(offset + ShopCatalogHeaderBytes, sizeof(uint)));
            if (!TryGetCapitalShopCatalogCurrency(
                    service,
                    packets[offset + 9],
                    frameItemId,
                    out var currency))
            {
                throw new InvalidDataException(
                    "Captured capital shop catalog has an invalid frame.");
            }
            BinaryPrimitives.WriteUInt32LittleEndian(
                packets.AsSpan(offset + 4, sizeof(uint)),
                npcId);
            BinaryPrimitives.WriteInt32LittleEndian(
                packets.AsSpan(offset + 12, sizeof(int)),
                Math.Max(0, balances.Get(currency)));
            offset += packetLength;
        }

        return packets;
    }

    public static bool TryResolveCapitalNpcShopOffer(
        CapitalNpcServiceKind service,
        int category,
        int listingIndex,
        uint expectedItemId,
        out CapitalShopOffer offer)
    {
        offer = default;
        if (category is < 0 or > byte.MaxValue ||
            listingIndex < 0 ||
            expectedItemId == 0 ||
            !CapitalNpcServiceProtocol.IsShop(service))
        {
            return false;
        }

        var packets = GetCapitalShopCatalogSource(service);
        var packetOffset = 0;
        // MSG_NPC_SELL carries Num as an index within the selected category,
        // not within the complete catalog stream. A category can span several
        // frames, so accumulate only records from matching-category frames.
        // Header byte 11 starts a new logical listing; continuation frames use
        // zero and append to the current listing.
        var categoryIndex = 0;
        while (packetOffset < packets.Length)
        {
            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(packetOffset));
            var frameCategory = packets[packetOffset + 8];
            var itemCount = packets[packetOffset + 10];
            if (frameCategory != category)
            {
                packetOffset += packetLength;
                continue;
            }

            if (packets[packetOffset + 11] != 0)
            {
                categoryIndex = 0;
            }

            for (var itemIndex = 0;
                 itemIndex < itemCount;
                 itemIndex++, categoryIndex++)
            {
                if (categoryIndex != listingIndex)
                {
                    continue;
                }

                // Min: the captured Lelantine frame is 4 bytes short of its last record, past every field read here.
                var recordStart = packetOffset + ShopCatalogHeaderBytes + (itemIndex * ShopCatalogItemBytes);
                var record = packets.AsSpan(recordStart, Math.Min(ShopCatalogItemBytes, packets.Length - recordStart));
                if (TryGetCapitalShopCatalogCurrency(
                        service,
                        packets[packetOffset + 9],
                        expectedItemId,
                        out var currency) &&
                    TryReadCapitalShopOffer(
                        record,
                        expectedItemId,
                        currency,
                        out offer))
                {
                    return true;
                }
            }
            packetOffset += packetLength;
        }

        return false;
    }

    /// <summary>
    /// Resolves the balance a catalog listing charges.
    /// </summary>
    /// <remarks>
    /// The Point Exchanger advertises a single frame-level currency code but
    /// really charges three balances, so its listings resolve per item. Every
    /// other capital shop keeps the frame-level currency the reference server
    /// captured.
    /// </remarks>
    private static bool TryGetCapitalShopCatalogCurrency(
        CapitalNpcServiceKind service,
        byte frameCurrency,
        uint itemId,
        out CapitalNpcShopCurrency currency)
    {
        if (service == CapitalNpcServiceKind.PointExchanger &&
            CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(
                itemId,
                out currency))
        {
            return true;
        }

        return CapitalNpcServiceProtocol.TryGetShopCurrency(
            frameCurrency,
            out currency);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long Revision, CapitalNpcServiceKind Service), byte[]>
        OverriddenCapitalShopCatalogs = new();

    private static byte[] GetCapitalShopCatalogSource(CapitalNpcServiceKind service)
    {
        var snapshot = NpcContentOverrides.Current;
        return snapshot.Shops.TryGetValue(service.ToString(), out var rows)
            ? OverriddenCapitalShopCatalogs.GetOrAdd(
                (snapshot.Revision, service),
                _ => BuildOverriddenCapitalShopCatalog(GetCompiledCapitalShopCatalogSource(service), rows))
            // W3 Magic Bell: appended here so the window and the purchase lookup read the same bytes.
            : WithMagicBellListing(service, GetCompiledCapitalShopCatalogSource(service));
    }

    // Lays editor rows out in the captured frame shape: same header, per-category currency and frame capacity.
    private static byte[] BuildOverriddenCapitalShopCatalog(
        byte[] compiled,
        IReadOnlyList<NpcShopOverrideRow> rows)
    {
        var headerTemplate = compiled.AsSpan(0, ShopCatalogHeaderBytes).ToArray();
        var recordTemplate = compiled.AsSpan(ShopCatalogHeaderBytes, ShopCatalogItemBytes).ToArray();
        var frameCapacity = 1;
        var categoryCurrency = new Dictionary<byte, byte>();
        for (var offset = 0; offset < compiled.Length;
             offset += BinaryPrimitives.ReadUInt16LittleEndian(compiled.AsSpan(offset)))
        {
            frameCapacity = Math.Max(frameCapacity, compiled[offset + 10]);
            categoryCurrency.TryAdd(compiled[offset + 8], compiled[offset + 9]);
        }

        // Plain record: no attributes, sockets, EXP or holy suit.
        recordTemplate.AsSpan(4, 20).Fill(0xFF);
        recordTemplate.AsSpan(28, 24).Clear();
        using var output = new MemoryStream();
        foreach (var category in rows.GroupBy(static r => r.Category))
        {
            var first = true;
            foreach (var chunk in category.Chunk(frameCapacity))
            {
                var frameLength = ShopCatalogHeaderBytes + (chunk.Length * ShopCatalogItemBytes);
                var frame = new byte[frameLength];
                headerTemplate.CopyTo(frame, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, checked((ushort)frameLength));
                frame[8] = category.Key;
                frame[9] = categoryCurrency.TryGetValue(category.Key, out var currency) ? currency : headerTemplate[9];
                frame[10] = checked((byte)chunk.Length);
                frame[11] = first ? (byte)1 : (byte)0;
                first = false;
                for (var index = 0; index < chunk.Length; index++)
                {
                    var record = frame.AsSpan(ShopCatalogHeaderBytes + (index * ShopCatalogItemBytes), ShopCatalogItemBytes);
                    recordTemplate.CopyTo(record);
                    BinaryPrimitives.WriteUInt32LittleEndian(record, chunk[index].ItemId);
                    record[24] = chunk[index].Quality;
                    record[25] = chunk[index].Grade;
                    record[26] = chunk[index].Bound;
                    record[27] = chunk[index].StackMarker;
                    BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(68), checked((uint)chunk[index].Price));
                }

                output.Write(frame);
            }
        }

        return output.ToArray();
    }

    /// <summary>The compiled listing of one shop, for the editor.</summary>
    public static IReadOnlyList<object> DescribeCompiledCapitalShop(CapitalNpcServiceKind service)
    {
        var compiled = GetCompiledCapitalShopCatalogSource(service);
        var rows = new List<object>();
        for (var offset = 0; offset < compiled.Length;
             offset += BinaryPrimitives.ReadUInt16LittleEndian(compiled.AsSpan(offset)))
        {
            for (var index = 0; index < compiled[offset + 10]; index++)
            {
                // The captured Lelantine frame is 4 bytes short of its last record; the fields read here are all present.
                var start = offset + ShopCatalogHeaderBytes + (index * ShopCatalogItemBytes);
                var record = compiled.AsSpan(start, Math.Min(ShopCatalogItemBytes, compiled.Length - start));
                rows.Add(new
                {
                    item = BinaryPrimitives.ReadUInt32LittleEndian(record),
                    price = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(68)),
                    category = (int)compiled[offset + 8],
                    currency = (int)compiled[offset + 9],
                    quality = (int)record[24],
                    grade = (int)record[25],
                    bound = (int)record[26],
                    stack_marker = (int)record[27]
                });
            }
        }

        return rows;
    }

    private static byte[] GetCompiledCapitalShopCatalogSource(
        CapitalNpcServiceKind service) =>
        service switch
        {
            CapitalNpcServiceKind.BoundGoldVendor =>
                BoundGoldVendorCatalog.Value,
            CapitalNpcServiceKind.BindingGoldShop =>
                BindingGoldShopCatalog.Value,
            CapitalNpcServiceKind.PointExchanger =>
                PointExchangerShopCatalog.Value,
            CapitalNpcServiceKind.AthensWarriorEquipmentVendor or
            CapitalNpcServiceKind.AthensScholarEquipmentVendor or
            CapitalNpcServiceKind.AthensJewelryEquipmentVendor or
            CapitalNpcServiceKind.AthensArmorEquipmentVendor or
            CapitalNpcServiceKind.AthensWarriorSupplier or
            CapitalNpcServiceKind.AthensSkillMerchant or
            CapitalNpcServiceKind.AthensArmorMerchant or
            CapitalNpcServiceKind.AthensScholarSupplier or
            CapitalNpcServiceKind.AthensJewelryMerchant or
            CapitalNpcServiceKind.AthensAlchemyRecipeVendor or
            CapitalNpcServiceKind.AthensIngredientsVendor or
            CapitalNpcServiceKind.AthensForgingRecipeVendor or
            CapitalNpcServiceKind.AthensMythcraftingRecipeVendor or
            CapitalNpcServiceKind.AthensScholarshipRecipeVendor =>
                GetCapturedAthensMerchantCatalogSource(service),
            // The Sparta merchant quarter captured on 2026-09-28.
            CapitalNpcServiceKind.SpartaWarriorEquipmentMerchant or
            CapitalNpcServiceKind.SpartaScholarEquipmentMerchant or
            CapitalNpcServiceKind.SpartaSkillMerchant or
            CapitalNpcServiceKind.SpartaArmorMerchant or
            CapitalNpcServiceKind.SpartaJewelryMerchant or
            CapitalNpcServiceKind.SpartaPropsMerchant or
            CapitalNpcServiceKind.SpartaPetMerchant =>
                GetCapturedSpartaMerchantCatalogSource(service),
            _ => GetCapturedCapitalShopCatalogSource(service)
        };

    private static bool TryReadCapitalShopOffer(
        ReadOnlySpan<byte> record,
        uint expectedItemId,
        CapitalNpcShopCurrency currency,
        out CapitalShopOffer offer)
    {
        offer = default;
        var itemId = BinaryPrimitives.ReadUInt32LittleEndian(record);
        var price = BinaryPrimitives.ReadUInt32LittleEndian(
            record.Slice(68));
        var socketCount = BinaryPrimitives.ReadInt16LittleEndian(
            record.Slice(34));
        var capturedStackMarker = record[27];
        if (itemId != expectedItemId ||
            price is 0 or > int.MaxValue ||
            capturedStackMarker == 0 ||
            socketCount is < 0 or > 4)
        {
            return false;
        }

        var sockets = new (short? EffectId, short? Level, short? Value)[4];
        for (var index = 0; index < socketCount; index++)
        {
            var encoded = BinaryPrimitives.ReadInt16LittleEndian(
                record.Slice(36 + (index * 2)));
            if (encoded <= 0)
            {
                return false;
            }
            sockets[index] = (
                checked((short)(encoded / 100)),
                checked((short)((encoded % 100) + 1)),
                BinaryPrimitives.ReadInt16LittleEndian(
                    record.Slice(44 + (index * 2))));
        }

        var item = new CompactItemEntry(
            itemId,
            ReadShopAttribute(record, 4),
            ReadShopAttribute(record, 8),
            ReadShopAttribute(record, 12),
            ReadShopAttribute(record, 16),
            ReadShopAttribute(record, 20),
            record[24],
            record[25],
            record[26],
            // Offset 27 is the stack field in the shared compact-item wire
            // shape. Shop captures repeat the same item and unit price with
            // 5/99 markers, so it is listing metadata, not grant authority.
            // Persistence derives granted stacks from the request quantity
            // and the authoritative item-template stack cap.
            1,
            BinaryPrimitives.ReadInt32LittleEndian(record.Slice(28)),
            BinaryPrimitives.ReadInt16LittleEndian(record.Slice(32)),
            null,
            null,
            null,
            null,
            null,
            socketCount,
            sockets[0].EffectId,
            sockets[0].Level,
            sockets[1].EffectId,
            sockets[1].Level,
            sockets[2].EffectId,
            sockets[2].Level,
            sockets[3].EffectId,
            sockets[3].Level,
            null,
            null,
            null,
            null)
        {
            Socket1Value = sockets[0].Value,
            Socket2Value = sockets[1].Value,
            Socket3Value = sockets[2].Value,
            Socket4Value = sockets[3].Value
        };
        var candidate = new CapitalShopOffer(
            item,
            checked((int)price),
            currency);
        if (!candidate.IsValid)
        {
            return false;
        }

        offer = candidate;
        return true;
    }

    private static int? ReadShopAttribute(
        ReadOnlySpan<byte> record,
        int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(
            record.Slice(offset));
        return value == -1 ? null : value;
    }

    private static byte[] InflateCatalog(
        string compressedBase64,
        int expectedLength,
        int expectedPacketCount,
        uint expectedCapturedNpcId)
    {
        using var compressed = new MemoryStream(
            Convert.FromBase64String(compressedBase64));
        using var gzip = new GZipStream(
            compressed,
            CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength);
        gzip.CopyTo(output);
        var packets = output.ToArray();
        if (packets.Length != expectedLength)
        {
            throw new InvalidDataException(
                "Captured capital shop stream has an invalid length.");
        }

        var offset = 0;
        var packetCount = 0;
        while (offset < packets.Length)
        {
            if (offset + 16 > packets.Length)
            {
                throw new InvalidDataException(
                    "Captured capital shop stream has a truncated header.");
            }

            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(offset, sizeof(ushort)));
            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(offset + 2, sizeof(ushort)));
            var capturedNpcId = BinaryPrimitives.ReadUInt32LittleEndian(
                packets.AsSpan(offset + 4, sizeof(uint)));
            if (packetLength < 16 ||
                offset + packetLength > packets.Length ||
                opcode != Opcodes.NpcShopCatalog ||
                capturedNpcId != expectedCapturedNpcId)
            {
                throw new InvalidDataException(
                    "Captured capital shop stream failed validation.");
            }

            packetCount++;
            offset += packetLength;
        }

        if (packetCount != expectedPacketCount)
        {
            throw new InvalidDataException(
                "Captured capital shop stream has an invalid packet count.");
        }

        return packets;
    }

    private static byte[] FilterUnsupportedCatalogItems(
        byte[] packets,
        uint[] unsupportedItemIds)
    {
        var filtered = (byte[])packets.Clone();
        var packetOffset = 0;
        while (packetOffset < packets.Length)
        {
            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(packetOffset));
            var itemCount = packets[packetOffset + 10];
            var payloadBytes = packetLength - ShopCatalogHeaderBytes;
            if (payloadBytes < 0 ||
                payloadBytes % ShopCatalogItemBytes != 0 ||
                itemCount > payloadBytes / ShopCatalogItemBytes)
            {
                throw new InvalidDataException(
                    "Captured capital shop item framing is invalid.");
            }

            var retainedCount = 0;
            for (var index = 0; index < itemCount; index++)
            {
                var recordOffset =
                    packetOffset + ShopCatalogHeaderBytes +
                    (index * ShopCatalogItemBytes);
                var itemId = BinaryPrimitives.ReadUInt32LittleEndian(
                    packets.AsSpan(recordOffset));
                if (Array.IndexOf(unsupportedItemIds, itemId) < 0)
                {
                    packets.AsSpan(recordOffset, ShopCatalogItemBytes)
                        .CopyTo(filtered.AsSpan(
                            packetOffset + ShopCatalogHeaderBytes +
                            (retainedCount * ShopCatalogItemBytes)));
                    retainedCount++;
                }
            }

            filtered[packetOffset + 10] = checked((byte)retainedCount);
            filtered.AsSpan(
                    packetOffset + ShopCatalogHeaderBytes +
                    (retainedCount * ShopCatalogItemBytes),
                    payloadBytes -
                    (retainedCount * ShopCatalogItemBytes))
                .Clear();
            packetOffset += packetLength;
        }

        return filtered;
    }
}
