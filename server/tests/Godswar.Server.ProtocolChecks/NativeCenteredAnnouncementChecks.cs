using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static class NativeCenteredAnnouncementChecks
{
    public const string CheckName = "Native red centered announcement framing and color";

    public static Task RunAsync()
    {
        // Native 10038: length/opcode/type/channel, followed by two terminated
        // 64-byte strings. SrvMsg.lua type50 concatenates them before rendering.
        var expected = Convert.FromHexString(
            "8900362732000000007C6346464646303030304265776172657C634646464646" +
            "4646460000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000000");
        Check.True(PacketBuilder.CenteredRedAnnouncement("Beware").SequenceEqual(expected),
            "red warning uses the independently pinned native frame and ARGB text tokens");

        var maximum = new string('W', 106);
        var packet = PacketBuilder.CenteredRedAnnouncement(maximum);
        Check.Equal(137, packet.Length, "red markup does not extend the native frame");
        Check.Equal((ushort)137, BinaryPrimitives.ReadUInt16LittleEndian(packet), "declared native size");
        Check.Equal((ushort)10038, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)), "native Python note");
        Check.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)), "direct-text formatter");
        Check.Equal((byte)0, packet[8], "centered proclamation, without a dialogue window");
        Check.True(packet[72] == 0 && packet[136] == 0, "both full native strings remain terminated");
        var visible = Encoding.ASCII.GetString(packet, 9, 63) + Encoding.ASCII.GetString(packet, 73, 63);
        Check.Equal("|cFFFF0000" + maximum + "|cFFFFFFFF", visible,
            "field-boundary concatenation preserves full warning, opaque red and white reset");

        var defaultPacket = PacketBuilder.CenteredAnnouncement("Victory");
        var defaultExpected = new byte[137];
        Convert.FromHexString("890036273200000000566963746F7279").CopyTo(defaultExpected, 0);
        Check.True(defaultPacket.SequenceEqual(defaultExpected),
            "existing uncolored completion announcement remains byte-for-byte unchanged");
        Check.Equal(126, PacketBuilder.CenteredAnnouncementMaximumTextLength,
            "default announcements keep their existing capacity");
        Check.Equal(106, PacketBuilder.CenteredRedAnnouncementMaximumTextLength,
            "red messages reserve both complete native markup tokens");
        foreach (var invalid in new[] { new string('W', 107), "Warning\0hidden", "Warning\n", "Flam\u00e9", "|cFFFFFFFFoverride" })
            Check.Throws<ArgumentOutOfRangeException>(() => PacketBuilder.CenteredRedAnnouncement(invalid),
                "red warnings reject truncated text, control bytes, unsupported encoding and nested color overrides");
        Check.Throws<ArgumentException>(() => PacketBuilder.CenteredRedAnnouncement(" "),
            "blank warning is not converted into a markup-only announcement");
        CheckPersonalGameLog();
        return Task.CompletedTask;
    }

    private static void CheckPersonalGameLog()
    {
        var expected = new byte[137];
        Convert.FromHexString("8900362732000000014F627461696E6564204C6576656C2034205361707068697265207820322E")
            .CopyTo(expected, 0);
        Check.True(PacketBuilder.PersonalGameLog("Obtained Level 4 Sapphire x 2.").SequenceEqual(expected),
            "literal native10038 direct50 channel1 text reaches the personal log, not the centered renderer or a dialogue");
        var maximum = new string('L', 126);
        var packet = PacketBuilder.PersonalGameLog(maximum);
        Check.True(packet.Length == 137 && packet[8] == 1 && packet[72] == 0 && packet[136] == 0 &&
            Encoding.ASCII.GetString(packet, 9, 63) + Encoding.ASCII.GetString(packet, 73, 63) == maximum,
            "left-log maximum length preserves both native field terminators and concatenates to the full text");
        foreach (var invalid in new[] { new string('L', 127), "Obtained\0hidden", "Obtained\n", "Sapphir\u00e9" })
            Check.Throws<ArgumentOutOfRangeException>(() => PacketBuilder.PersonalGameLog(invalid),
                "left log rejects overflow, embedded terminators, controls and unsupported encoding");
        Check.Throws<ArgumentException>(() => PacketBuilder.PersonalGameLog(" "), "left log requires visible text");
    }
}
