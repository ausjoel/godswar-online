using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // Records per 10071 frame. The client takes up to 32 (Origin.exe 0x48c159);
    // the captured shops never send more than 16, so neither does the GM Mall.
    private const int GmMallRecordsPerFrame = 16;
    private const byte GmMallSilverCurrency = 3;
    internal const uint GmMallAdvertisedPrice = 1;

    /// <summary>
    /// One GM Mall window as opcode-10071 shop catalog frames addressed to a
    /// visible NPC. Frame layout is the captured one (16-byte header, 88-byte
    /// records): +8 tab, +9 currency (3 = silver), +10 records, +11 first frame
    /// of a tab, +12 balance. The first frame is tab 0 with +11 = 1, which makes
    /// the client clear all four tabs of that NPC first (0x48c163), so a previous
    /// window never leaks into this one.
    /// </summary>
    public static byte[] GmMallCatalog(
        uint npcId,
        GmMallPage page,
        int silverBalance,
        Func<uint, byte> stackMarker)
    {
        ArgumentNullException.ThrowIfNull(page);
        var template = BindingGoldShopCatalog.Value;
        var headerTemplate = template.AsSpan(0, ShopCatalogHeaderBytes).ToArray();
        var recordTemplate = template.AsSpan(ShopCatalogHeaderBytes, ShopCatalogItemBytes).ToArray();
        // Plain record: no attributes, sockets, EXP or holy suit.
        recordTemplate.AsSpan(4, 20).Fill(0xFF);
        recordTemplate.AsSpan(28, 24).Clear();

        using var output = new MemoryStream();
        for (var tab = 0; tab < page.Tabs.Length && tab < GmMallCatalogTabs; tab++)
        {
            var first = true;
            foreach (var chunk in page.Tabs[tab].ItemIds.Chunk(GmMallRecordsPerFrame))
            {
                var frameLength = ShopCatalogHeaderBytes + (chunk.Length * ShopCatalogItemBytes);
                var frame = new byte[frameLength];
                headerTemplate.CopyTo(frame, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, checked((ushort)frameLength));
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), Opcodes.NpcShopCatalog);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), npcId);
                frame[8] = (byte)tab;
                frame[9] = GmMallSilverCurrency;
                frame[10] = checked((byte)chunk.Length);
                frame[11] = first ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), Math.Max(0, silverBalance));
                first = false;
                for (var index = 0; index < chunk.Length; index++)
                {
                    var record = frame.AsSpan(
                        ShopCatalogHeaderBytes + (index * ShopCatalogItemBytes),
                        ShopCatalogItemBytes);
                    recordTemplate.CopyTo(record);
                    BinaryPrimitives.WriteUInt32LittleEndian(record, chunk[index]);
                    record[24] = 1; // quality
                    record[25] = 1; // grade
                    record[26] = 0; // tradable
                    record[27] = Math.Max((byte)1, stackMarker(chunk[index]));
                    BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(68), GmMallAdvertisedPrice);
                }

                output.Write(frame);
            }
        }

        return output.ToArray();
    }

    private const int GmMallCatalogTabs = 4;
}
