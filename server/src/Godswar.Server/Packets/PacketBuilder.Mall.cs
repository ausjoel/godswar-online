using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    /// <summary>
    /// The reply to the client's <c>C2S 10067</c> click on the mall npc, as the
    /// reference server sent it for Athens_074 (npc 5212).
    /// </summary>
    /// <remarks>
    /// The reference answers <em>every</em> npc click with this 48-byte frame; the
    /// mall npc is the one that carries flags <c>0x200</c> and the
    /// <c>Athens_074</c> key. Captured layout: <c>+4</c> the npc, <c>+8</c> flags,
    /// <c>+12</c> a packed word the capital shops leave zero, then the npc key. The
    /// ordinary shops advertise flags 4 and the quest page 3.
    /// </remarks>
    private static readonly byte[] MallDialogOpenBytes = Convert.FromHexString(
        "300053275c14000000020000504efb02417468656e735f30373400000000000000000000000000000000000000000000");

    /// <summary>
    /// The mall window frames, keyed by window id 765: the window definition, its
    /// category list, and the two frames that travel with them.
    /// </summary>
    /// <remarks>
    /// All four were captured together right after the client chose the mall's
    /// first sub-menu entry, and none of them carries the npc id, so they are
    /// replayed verbatim for whichever mall npc is open. Recorded fields:
    /// <list type="bullet">
    /// <item>10021: window 765, script id 6148, script name <c>BOGART-</c>, layout
    /// and the item slots after offset 160.</item>
    /// <item>10201: window 765, then the category ids 8061 to 8066.</item>
    /// <item>10248: window 765 and the page fields.</item>
    /// <item>10199: window 765, script name <c>Luminay</c>, and 2000.</item>
    /// </list>
    /// </remarks>
    private static readonly byte[][] MallWindowFrames =
    [
        Convert.FromHexString(
            "04012527fd02000004180000424f474152542d00000000000000000000000000" +
            "0000000000000000000000009f8800009f880000010159000000033503120042" +
            "9001adc24c67cc3f9a99993f008009450596869696868686969696ca11110000" +
            "0000000000000404040404040404040404000000000000000000000063091f0c" +
            "f70a3b082b0abb0b5b0b8f0a860c860c0f07851fb43700000000000000000000" +
            "0000000000000000ff171000d0070000010005006004000005020000f1030000" +
            "fd010000fc010000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000" +
            "00000000"),
        Convert.FromHexString(
            "4000d927fd02000035000000010000007d1f0000000000007e1f00007f1f0000" +
            "801f000000000000811f0000821f000000000000000000000000000000000000"),
        Convert.FromHexString(
            "6c0008288b840300fd0200000a00720000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000" +
            "0100000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000000000000"),
        Convert.FromHexString(
            "5000d727fd0200004c756d696e61727900000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000001010100d0070000")
    ];

    /// <summary>
    /// The mall's stock list: <c>S2C 10022</c>, 1524 bytes, window 765, captured at
    /// 22:10:02.367 — the first frame after the mall window was opened that actually
    /// carries goods (76 twenty-byte records, each an item slot with its price).
    /// </summary>
    /// <remarks>
    /// Replayed verbatim: the reference sent it unprompted 3.6 s after answering the
    /// window's page <c>601</c>, and nothing in the capture shows a purchase, so the
    /// landed prices are the reference's own numbers.
    /// </remarks>
    private static readonly byte[] MallStockFrame = Convert.FromHexString(
        "f4052627fd02000063090000160000005a0000002800000083000000ffffffff" +
        "0609010160e561652d0100000000000000000000000000000000000000000000" +
        "0000000000000000cf1dfa4e3d4606001f0c0000320000008e00000046000000" +
        "67000000ffffffff0608010160f40d002d010000000000000000000000000000" +
        "000000000000000000000000000000000968378ed5450600f70a000016000000" +
        "5a000000280000003c000000ffffffff0609010160f40d002d01000000000000" +
        "000000000000000000000000000000000000000000000000243797d43a460600" +
        "3b0800000b000000650000008300000032000000ffffffff0609010160f40d00" +
        "2d01000000000000000000000000000000000000000000000000000000000000" +
        "c86d0fd1d54506002b0a00000c000000200000004600000028000000ffffffff" +
        "0608010160f40d002d0100000000000000000000000000000000000000000000" +
        "00000000000000004dd10c6e3c460600bb0b00000c0000002100000084000000" +
        "8e000000ffffffff0608010160f40d002d010000000000000000000000000000" +
        "00000000000000000000000000000000cb246588d54506005b0b000032000000" +
        "6600000084000000a3000000ffffffff0608010160f40d002d01000000000000" +
        "000000000000000000000000000000000000000000000000d7a69d88d5450600" +
        "8f0a00000c000000200000004600000032000000ffffffff0609010160f40d00" +
        "2d01000000000000000000000000000000000000000000000000000000000000" +
        "01dd8b2502460600860c0000160000005a0000003c00000084000000ffffffff" +
        "0609010160046c5f2d0100000000000000000000000000000000000000000000" +
        "0000000000000000cbf3587d3d460600860c0000160000005a0000003c000000" +
        "84000000ffffffff0609010160f40d002d010000000000000000000000000000" +
        "000000000000000000000000000000009c87d888d54506000f07000016000000" +
        "5a0000003c00000084000000ffffffff0a0c010160046c5f2d01000000000000" +
        "000000000000000000000000000000000000000000000000d0edfe8dd5450600" +
        "851f0000ffffffffffffffffffffffffffffffffffffffff0101010100000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "4f7752aec04a0600b4370000ffffffffffffffffffffffffffffffffffffffff" +
        "0101010100000000000000000000000000000000000000000000000000000000" +
        "0000000000000000cb788a023b460600ffffffffffffffffffffffffffffffff" +
        "ffffffffffffffff010100010000000000000000000000000000000000000000" +
        "00000000000000000000000000000000ffffffffffffffffffffffffffffffff" +
        "ffffffffffffffffffffffffffffffff01010001000000000000000000000000" +
        "000000000000000000000000000000000000000000000000ffffffffffffffff" +
        "ffffffffffffffffffffffffffffffffffffffffffffffff0101000100000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" +
        "0101000100000000000000000000000000000000000000000000000000000000" +
        "0000000000000000ffffffffffffffffffffffffffffffffffffffffffffffff" +
        "ffffffffffffffff010100010000000000000000000000000000000000000000" +
        "00000000000000000000000000000000ffffffffffffffffffffffffffffffff" +
        "ffffffffffffffffffffffffffffffff01010001000000000000000000000000" +
        "000000000000000000000000000000000000000000000000ffffffffffffffff" +
        "ffffffffffffffffffffffffffffffffffffffffffffffff0101000100000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" +
        "0101000100000000000000000000000000000000000000000000000000000000" +
        "0000000000000000ffffffffffffffffff171000");

    /// <summary>
    /// The mall window's owner frame: <c>S2C 10166</c>, 236 bytes, window 765, sent
    /// together with the stock list.
    /// </summary>
    private static readonly byte[] MallOwnerFrame = Convert.FromHexString(
        "ec00b627fd020000424f474152542d0000000000000000000000000000000000" +
        "0000000000000000010001007e5dc342000000000000adc29a99993f00000100" +
        "14000000000000000000000001000000f7530e00a00000000400000003000000" +
        "35dcf538590000009f880000400e0000010098083500010000fd4902cf000000" +
        "000000000000000079790700000000009f880000400e0000a0010000b4010000" +
        "ce020000c3070000b80d00001604000045020000ef0100006a00000042000000" +
        "00000000f0a7063f5d0600000000000000000000000000000000000001000000" +
        "98080000bca2020005000000");

    /// <summary>Opens the mall page for one npc, as the capture did.</summary>
    public static byte[] NpcMallDialogOpenAck(uint npcId, int extraFlags = 0)
    {
        var packet = (byte[])MallDialogOpenBytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8, 4),
            CapitalNpcServiceProtocol.MallOpenFlags | extraFlags);
        return packet;
    }

    /// <summary>The mall window frames, in the order the capture sent them.</summary>
    public static IReadOnlyList<byte[]> MallWindow() =>
        [.. MallWindowFrames.Select(static frame => (byte[])frame.Clone())];

    /// <summary>
    /// The mall's goods: the stock list followed by the window owner frame, in the
    /// order and pairing the reference sent them (22:10:02.367).
    /// </summary>
    public static IReadOnlyList<byte[]> MallStock() =>
        [(byte[])MallStockFrame.Clone(), (byte[])MallOwnerFrame.Clone()];
}
