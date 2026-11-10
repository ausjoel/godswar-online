using System.Buffers.Binary;
using System.IO.Compression;
using Godswar.Server.Application.Npc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // The cash mall catalog the client asks for with C2S 10178. The reference
    // answers with fifteen S2C 10178 frames, one page each: 16 records per full
    // frame, 96/1328/1152-byte frames carrying 1/15/13. Captured twice - Sparta
    // on 2026-09-13 01:24 and Athens on 2026-09-14 07:10 - and both streams have
    // the same frame lengths, the same category ids 1792..1798, the same tabs and
    // the same prices. They differ in exactly twelve records, where each camp
    // advertises its own item (Sparta 4629..4634 and 4304..4319 against Athens
    // 4614..4619 and 4300..4318), so each camp keeps its own captured stream.
    private const int MallCatalogFrameCount = 15;
    private const int MallCatalogHeaderBytes = 8;
    private const int MallCatalogItemBytes = 88;

    private const string SpartaMallCatalogGzip =
            "H4sIAAAAAAACCrXbS2xMcRTH8Tudqako9047bU3jlUyYIBXPEq82ESQiQrHRVoQpbQWhSOvZiFBWNkQEsRCvIo1W2LCwour9bqdI" +
            "hHikGyFBJHWHLiT3jEj+5ztJF130k1/OPf9z//c/tw3pN2JW0PGNjVpWt/Dx+Syf9R+fLH93d95fv8fdn+QfjlNwawR3vIJblOZ1" +
            "pyq4vYW80xTciYI7XcH9IbhFCm6zUN9ZCm6bz+vOhtz5UP8uUHDfCW4JdN2WQO4ex9zNFOqwV8EtE9wGBfeY0GcNf+bwv+oRgeoR" +
            "geoRManHPoU6H/Z73UoFd7hQhyoFt1pwqxVcR1h3G6A61EB12AjVYRtUh+1QHXZAdTiq4EYDXneCwn2jXnALFdxEutddZiXnsM8q" +
            "s8398hRz3ufut+OG+dNS3P8rIHcl5K6C3ErIrYLcashdDbm1kFsHuVsgdyvkboPc7ZC7A3J3Qm7PHLbOh8z9mYJ/zjZ3pX18o4I7" +
            "SHBvQnlvQXkPQdct6Ji7V/p43U/9zN2FQt5TDrM+TkPuGcg9C7npDtO/JxT6Ny64k6D+LVNYx6n2w2nufvhtoZmf7foZQaHfbHO3" +
            "xWLc3X7GlepwwTF3rQKvG1Bwvwh9Ec4yd6uE65YDubmQmwe5/SE3Arn5kOsfae4OyBTWhYIbFs4nxiu4D22vO/r3HLatAsgfBblj" +
            "IPdSyNyNCv3WrODOEtwWBTchuJcV3CPCfL+vcN8ICnkfQG4H5LZDbgJyXym4tuD20tif9Pa6mxT6ty3FvtXv7luDhvMn3/V/Znj9" +
            "ppjsBoP/716fKpxLQO4FyD0LuY2Q29dh+qE6Zu72EdZdSCGv9HybBeXNjjF5h0F5Y1DeyVDeKVDeuZDbM4etFQr+LsGPK7gHhPtH" +
            "jYJbLLgbFVzpnNFRmBPS+domqI83K7hrpe+RYkwd1kN5Nyi4/YQ+q4PyJmxzd6zgdiq40vnBS5vph28jzN150vsuCtet1i/P4YC7" +
            "H75meI6bl3xezPH6pVFzt12oR5mC2yKc05QruIPCXrfENndrhbxHQubuSWFONEHu42Hm7hbhvaInkPsUcp9B7nPIfQG57ZDbAbkJ" +
            "yO2Zw1Yn5LcquNOEOXwbctsg9w7k3oXce5B7H3IfQO5DyH2v4Er/J/JBwc0V1vFHBbcr5HU/KbjtUeF8OGbuFgnnakEF90uK8+F0" +
            "dz+cMcTMz03xfX4W5K4bzLh1kPtawZXOWz9C7hrb3JWeb1cquNLz7VoFV3q+XQHVIQ7VoQKqw4AhTJ9NhtzlNuMWK6y3KUJ96wPJ" +
            "OdzXOqngdwj+HCj3RSjvHijvVSjvLShvK5T3EZT3MZR3EZS3FMq7FMpbAeVN7gN7ufvAfdlmfiTF3DwIuccgtwlyF4QZtxxyKyB3" +
            "DeRGchh3KOQWQG4x5H6D3EAu42ZC7huoDj1z2NqvMH/qhfd9j0PuKchthNySMOMuhubaZijvVsjNz2HcgdC6K4TcGZDbBdX3M+R+" +
            "herwHcr7C8jcpQVwTAAA";

    private const string AthensMallCatalogGzip =
            "H4sIAAAAAAACCrXbS2xMcRTH8TudqdtquXfaaWXEK5kwQSqeJV5tIkhEhGKjrQhTtIJQpPVsRCgrGyKNioV4FWmosGFhRdX70aoS" +
            "iRCP2AgJIqk7dCG5Z0TyP99Juuiin/xy7vmf+7//ua1PvxG3bDcwNmZZ3cInELAC1n98coLd3f3++j3h/ST/cJyCWy244xXcojS/" +
            "O1XBzRTyTlNwJwrudAX3h+AWKbiXhPrOUnDbAn53NuTOh/p3gYL7VnBLoOu2BHL3uuZutlCHfQpumeDWK7iNQp/V/5nD/6pHFKpH" +
            "FKpH1KQe+xXqfCTod1cruMOFOlQquFWCW6XgusK62wjVoRqqwyaoDtuhOuyA6rATqsNRBTcW8rsTFO4bdYJbqOB2pfvdZVZyDges" +
            "MsfcL08x5wPefjthmD8txf2/AnJXQu4qyF0NuZWQWwW5ayC3BnJrIXcr5G6D3O2QuwNyd0LuLsjtmcPWubC5P1PwzzrmrrSPb1Jw" +
            "BwnuTSjvLSjvYei62a65eyXL737sa+4uFPKedJn1cQpyT0PuGchNd5n+Pa7QvwnBnQT1b5nCOk61H07z9sNvCs38XM/PsIV+c8zd" +
            "Fotx9wQZV6rDedfctQr8bkjB/SL0RSTH3K0UrlsvyLUhNwNyMyG3N+RmQW5wpLk7IFtYFwpuRDifGK/gPnT87ujfc9ixCiB/FOSO" +
            "gdyLYXM3JvTbJQV3luC2KLhdgntZwW0Q5nubwn3DFvLegdx2yO2A3BeQ+1LBdaT7ssb+JNPvblbo37YU+9agt2+1DedPf8//meH3" +
            "m+Oya9v/716fKpxLQO55yD0DuU2Q28dl+qEqbu5mCesurJBXer7NgfLmxpm8w6C8cSjvZCjvFCjvXMjtmcPWCgV/t+AnFNyDwv2j" +
            "WsEtFtxNCq50zugqzAnpfG0z1MdbFNx10vdIcaYOG6C8GxXcvkKf1UJ5uxxzd6y0H1ZwpfODlw7TD99GmLvzpPddFK5bTVCewyFv" +
            "P3zN8By3X/J5Mc/vl8bM3U6hHmUKbotwTlOu4A6K+N0Sx9ytEfI2hM3dE8KcaIbcx8PM3a3Ce0VPIPcp5LZDbgfkPoPcTsh9Drld" +
            "kNszh60XkN+q4E4T5vBtyG2D3DuQexdy70Hufch9ALkPIfedgiv9n8h7BTdfWMcfFNxPYb/7UcHtjAnnw3Fzt0g4V7MV3C8pzofT" +
            "vf1wxhAzPz/F9/k5kLt+MOPWQu4rBVc6b/0AuWsdc1d6vl2p4ErPt+sUXOn5dgVUhwRUhwqoDgOGMH02GXKXO4xbrLDepgj1rQsl" +
            "53Af64SC/1zw50C5L0B590J5r0J5b0F5W6G8j6C8j6G8i6C8pVDepVDeCihvch/Yy9sH7s8186Mp5uYhyG2E3GbIXRBh3HLIrYDc" +
            "tZAbzWPcoZBbALnFkPsNckP5jJsNua+hOvTMYeuAwvypE973PQa5JyG3CXJLIoy7GJprW6C82yC3fx7jDoTWXSHkzoDcT1B9P0Pu" +
            "V6gO36G8vwAYqj4scEwAAA==";

    private static readonly Lazy<byte[]> SpartaMallCatalog = new(
        () => InflateMallCatalog(SpartaMallCatalogGzip, 19_568, "Sparta"));

    private static readonly Lazy<byte[]> AthensMallCatalog = new(
        () => InflateMallCatalog(AthensMallCatalogGzip, 19_568, "Athens"));

    /// <summary>The camp's captured mall catalog, complete frames in one buffer.</summary>
    // Editor rows: MallAthens and MallSparta replace that camp's whole catalog. MallAdmin is tab 7, which the
    // captured catalog leaves empty; only editor_admin accounts are sent it or can buy from it.
    internal const string MallAthensService = "MallAthens";
    internal const string MallSpartaService = "MallSparta";
    internal const string MallAdminService = "MallAdmin";
    internal const byte MallAdminCategory = 7;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long Revision, byte Camp, bool Admin), byte[]>
        OverriddenMallCatalogs = new();

    private static byte[] MallCatalogSource(byte camp, bool admin)
    {
        var captured = camp == GameDefaults.AthensCamp ? AthensMallCatalog.Value : SpartaMallCatalog.Value;
        var snapshot = NpcContentOverrides.Current;
        snapshot.Shops.TryGetValue(camp == GameDefaults.AthensCamp ? MallAthensService : MallSpartaService, out var rows);
        IReadOnlyList<NpcShopOverrideRow>? adminRows = null;
        if (admin) snapshot.Shops.TryGetValue(MallAdminService, out adminRows);
        if (rows is null && adminRows is null) return captured;
        return OverriddenMallCatalogs.GetOrAdd((snapshot.Revision, camp, admin), _ =>
        {
            using var output = new MemoryStream();
            if (rows is null) output.Write(captured);
            else WriteMallFrames(output, captured, rows.GroupBy(static r => r.Category));
            if (adminRows is not null)
                WriteMallFrames(output, captured, [.. adminRows.GroupBy(static _ => MallAdminCategory)]);
            return output.ToArray();
        });
    }

    private static void WriteMallFrames(MemoryStream output, byte[] captured,
        IEnumerable<IGrouping<byte, NpcShopOverrideRow>> categories)
    {
        var recordTemplate = captured.AsSpan(MallCatalogHeaderBytes, MallCatalogItemBytes).ToArray();
        // Plain record: no attributes, sockets, EXP or holy suit.
        recordTemplate.AsSpan(4, 20).Fill(0xFF);
        recordTemplate.AsSpan(28, 24).Clear();
        foreach (var category in categories)
        {
            var first = true;
            foreach (var chunk in category.Chunk(16))
            {
                var frameLength = MallCatalogHeaderBytes + (chunk.Length * MallCatalogItemBytes);
                var frame = new byte[frameLength];
                captured.AsSpan(0, MallCatalogHeaderBytes).CopyTo(frame);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, checked((ushort)frameLength));
                frame[4] = category.Key;
                frame[6] = checked((byte)chunk.Length);
                frame[7] = first ? (byte)1 : (byte)0;
                first = false;
                for (var index = 0; index < chunk.Length; index++)
                {
                    var record = frame.AsSpan(MallCatalogHeaderBytes + (index * MallCatalogItemBytes), MallCatalogItemBytes);
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
    }

    public static IReadOnlyList<object> DescribeCompiledMall(byte camp)
    {
        var captured = camp == GameDefaults.AthensCamp ? AthensMallCatalog.Value : SpartaMallCatalog.Value;
        var rows = new List<object>();
        for (var offset = 0; offset < captured.Length;
             offset += BinaryPrimitives.ReadUInt16LittleEndian(captured.AsSpan(offset)))
        {
            for (var index = 0; index < captured[offset + 6]; index++)
            {
                var record = captured.AsSpan(offset + MallCatalogHeaderBytes + (index * MallCatalogItemBytes));
                rows.Add(new
                {
                    item = BinaryPrimitives.ReadUInt32LittleEndian(record),
                    price = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(68)),
                    category = (int)captured[offset + 4],
                    currency = 0,
                    quality = (int)record[24],
                    grade = (int)record[25],
                    bound = (int)record[26],
                    stack_marker = (int)record[27]
                });
            }
        }
        return rows;
    }

    public static byte[] MallCatalog(byte camp, bool admin = false) =>
        (byte[])MallCatalogSource(camp, admin).Clone();

    /// <summary>
    /// Resolves one mall listing. The mall frames carry the same 88-byte compact
    /// item record the capital shop catalogs use, so the item, its price at
    /// <c>+68</c> and the mall's own currency resolve through the shop reader.
    /// </summary>
    /// <remarks>
    /// Frame header: <c>+4</c> the category, <c>+6</c> the item count, <c>+7</c>
    /// set on the first frame of a category. The client addresses a listing by
    /// category plus an index that restarts on each new category frame, exactly
    /// like MSG_NPC_SELL, so the index accumulates only across matching frames.
    /// </remarks>
    public static bool TryResolveMallCatalogOffer(
        byte camp,
        int category,
        int listingIndex,
        uint expectedItemId,
        out CapitalShopOffer offer,
        bool admin = false)
    {
        offer = default;
        if (category is < 0 or > byte.MaxValue ||
            listingIndex < 0 ||
            expectedItemId == 0)
        {
            return false;
        }

        var packets = MallCatalogSource(camp, admin);
        var packetOffset = 0;
        var categoryIndex = 0;
        while (packetOffset < packets.Length)
        {
            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(packetOffset));
            if (packets[packetOffset + 4] != category)
            {
                packetOffset += packetLength;
                continue;
            }

            if (packets[packetOffset + 7] != 0)
            {
                categoryIndex = 0;
            }

            var itemCount = packets[packetOffset + 6];
            for (var itemIndex = 0;
                 itemIndex < itemCount;
                 itemIndex++, categoryIndex++)
            {
                if (categoryIndex != listingIndex)
                {
                    continue;
                }

                var record = packets.AsSpan(
                    packetOffset + MallCatalogHeaderBytes +
                    (itemIndex * MallCatalogItemBytes),
                    MallCatalogItemBytes);
                if (TryReadCapitalShopOffer(
                        record,
                        expectedItemId,
                        category == MallAdminCategory ? CapitalNpcShopCurrency.Silver : CapitalNpcServiceProtocol.MallCurrency,
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
    /// Resolves a mall listing from its item id alone.
    /// </summary>
    /// <remarks>
    /// The reference capture holds no mall purchase, so the category and index the
    /// client sends for one are unverified. This is the documented fallback for that
    /// gap: the item still has to be in the captured catalog and its price still
    /// comes from the captured record, so nothing is invented. The caller logs every
    /// use so the real request shape can be pinned from a live purchase.
    /// </remarks>
    public static bool TryResolveMallCatalogOfferByItem(
        byte camp,
        uint expectedItemId,
        out CapitalShopOffer offer)
    {
        offer = default;
        if (expectedItemId == 0)
        {
            return false;
        }

        var packets = MallCatalogSource(camp, admin: false);
        var packetOffset = 0;
        while (packetOffset < packets.Length)
        {
            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(packetOffset));
            var itemCount = packets[packetOffset + 6];
            for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
            {
                var record = packets.AsSpan(
                    packetOffset + MallCatalogHeaderBytes +
                    (itemIndex * MallCatalogItemBytes),
                    MallCatalogItemBytes);
                if (TryReadCapitalShopOffer(
                        record,
                        expectedItemId,
                        CapitalNpcServiceProtocol.MallCurrency,
                        out offer))
                {
                    return true;
                }
            }

            packetOffset += packetLength;
        }

        return false;
    }

    private static byte[] InflateMallCatalog(
        string compressedBase64,
        int expectedLength,
        string camp)
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
                $"Captured {camp} mall catalog has an invalid length.");
        }

        var offset = 0;
        var frameCount = 0;
        while (offset < packets.Length)
        {
            if (offset + MallCatalogHeaderBytes > packets.Length)
            {
                throw new InvalidDataException(
                    $"Captured {camp} mall catalog has a truncated header.");
            }

            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(offset, sizeof(ushort)));
            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(offset + 2, sizeof(ushort)));
            var itemCount = packets[offset + 6];
            if (packetLength < MallCatalogHeaderBytes ||
                offset + packetLength > packets.Length ||
                opcode != Opcodes.MallCatalog ||
                MallCatalogHeaderBytes +
                    (itemCount * MallCatalogItemBytes) != packetLength)
            {
                throw new InvalidDataException(
                    $"Captured {camp} mall catalog failed validation.");
            }

            frameCount++;
            offset += packetLength;
        }

        if (frameCount != MallCatalogFrameCount)
        {
            throw new InvalidDataException(
                $"Captured {camp} mall catalog has an invalid frame count.");
        }

        return packets;
    }
}
