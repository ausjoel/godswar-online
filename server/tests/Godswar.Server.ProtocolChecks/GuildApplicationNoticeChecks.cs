using System.Buffers.Binary;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The guild window's feedback lines: <c>S2C 10038</c> on the personal channel,
/// which the client's own <c>SrvMsg.lua</c> prints in its lower-right log
/// (<c>CHANNEL_PRESONAL</c> -> <c>GameAPI:AddPersonalMessage_UTF8</c>).
/// </summary>
/// <remarks>
/// The payload is the client's own text, in the client's own encoding: the frame
/// is decoded with the machine's ANSI code page before Lua prints it, so the
/// guild feedback carries the exact bytes of the entry the client ships in
/// <c>Localization/zh_cn/Text/ErrorMessage.dat</c> (here the GBK bytes of
/// <c>ERROR_03D8 公会会长副会长都不在线 无法加入</c>) instead of an English line
/// this server would have invented.
/// </remarks>
internal static class GuildApplicationNoticeChecks
{
    public const string CheckName =
        "Guild application feedback on the personal channel (10038)";

    private static readonly byte[] NoOfficerOnlineText =
        Convert.FromHexString(
            "B9ABBBE1BBE1B3A4B8B1BBE1B3A4B6BCB2BBD4DACFDF20CEDEB7A8BCD3C8EB");

    public static Task RunAsync()
    {
        var packet = PacketBuilder.PersonalNotice(NoOfficerOnlineText);
        Check.Equal(137, packet.Length, "personal notice packet length");
        Check.Equal(
            (ushort)137,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "personal notice declared length");
        Check.Equal(
            (ushort)10038,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "personal notice opcode");
        Check.Equal(
            50,
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)),
            "direct-text note type the client's Lua concatenates");
        Check.Equal(1, packet[8], "personal channel (the lower-right log)");
        Check.True(
            packet.AsSpan(9, NoOfficerOnlineText.Length)
                .SequenceEqual(NoOfficerOnlineText),
            "the client's own bytes are carried verbatim");
        Check.Equal(
            0,
            packet[9 + NoOfficerOnlineText.Length],
            "the first text field stays NUL-terminated");
        Check.Equal(0, packet[73], "the second text field is left empty");
        return Task.CompletedTask;
    }
}
