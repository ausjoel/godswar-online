using System.Buffers.Binary;
using System.IO.Compression;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // Point Exchanger (Sparta_077 / Athens_077, interaction 5074) shop catalog.
    //
    // Captured from the reference server on 2026-09-13 as five opcode-10071
    // frames. Two independent captures produced byte-identical frame sizes
    // (1424 / 104 / 1336 / 984 / 984), so this is the authoritative stream.
    //
    // The framing is exactly the one already used by the working B-Gold vendor
    // and BindingGoldShop catalogs: a 16-byte frame header followed by
    // 88-byte item records, with
    //     frame +8  category         frame +9  currency (0x04 = B-Gold)
    //     frame +10 item count       frame +11 new-listing flag
    //     frame +12 u32 balance      (patched at egress)
    //     record +0  item id         record +64 listing marker
    //     record +68 unit price      record +84 quantity
    // The reference server's final record in each frame arrives four bytes
    // shorter than the full stride. Because the rest of the catalog family and
    // the client vendor window both address records at a fixed 88-byte stride,
    // that tail is zero-padded back to full stride here. The padded bytes are
    // the trailing quantity field, which the server never derives a grant from.
    //
    // Dropped item ids: [14073]. These have no ItemBaseAttribute entry in the
    // client, and advertising them makes Origin dereference a missing
    // definition while building the vendor window - the same reason
    // UnsupportedBoundGoldVendorItemIds exists in PacketBuilder.CapitalNpcShops.
    private const string PointExchangerShopCatalogGzip =
        "H4sIAOKWpWoC/73WO0vEQBAH8M1LESGXoHBNioCQFJaCiGB7sZLzGkVLLcRaGxtt7SzkuE9g4QP8CBZ+Aonv8/2EAyutY7i7aGD2QG7n7xaBLOQ3ww6ZnS1rJjwZEEKYjpY+RRwIkUiWli7xh1U0k8Sxf9+zj05B7hnIPQe5FyD3EuRegdxrkFsHuTcg91XV7UmSQ5O6byC3wXAO5RJ1e0PM+a46iq6VuiF11x31fP0p6i6JrB+3djYZ4my7NM5Yu+9rZqG5VRpSi+OlcY77aJxuXS3n7th8rp5zPZfPNXJuXORzzZz7OULdCFS3CFS3CFS3CFS3CFS3MkPdPnTqTjO4dYu6FQZX1ofmQO5El+5GtfbjSuc3o9U3dbO/uTVfUMvfT+8X36Nx1hjcyYC6X6OKboe54DZQz1d2z96B3HuQ+wByH0HuE8h9BrkvIDf7r432f72sOI/qaZxFyb24wuBWDOouMLiDkr5fddXdWck9tQ9yG7a660vOd1dxDtc7zPt7DO67JN8DBrc2/n+uPqxet6NYI+43SspdDuASAAA=";

    private static readonly Lazy<byte[]> PointExchangerShopCatalog = new(
        () => InflatePointExchangerShopCatalog(
            PointExchangerShopCatalogGzip,
            expectedLength: 4832,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5074u));

    private static byte[] InflatePointExchangerShopCatalog(
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
                "Captured point exchanger shop stream has an invalid length.");
        }

        var offset = 0;
        var packetCount = 0;
        while (offset < packets.Length)
        {
            if (offset + 16 > packets.Length)
            {
                throw new InvalidDataException(
                    "Captured point exchanger shop stream has a truncated header.");
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
                    "Captured point exchanger shop stream failed validation.");
            }

            packetCount++;
            offset += packetLength;
        }

        if (packetCount != expectedPacketCount)
        {
            throw new InvalidDataException(
                "Captured point exchanger shop stream has an invalid packet count.");
        }

        return packets;
    }
}
