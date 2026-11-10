using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int PythonNotePacketLength = 137;
    private const int PythonNoteFieldLength = 64;
    private const int PythonNoteTextPartLength = PythonNoteFieldLength - 1;
    internal const int CenteredAnnouncementMaximumTextLength =
        PythonNoteTextPartLength * 2;
    private const int PythonNoteDirectTextType = 50;
    private const byte PythonNoteCenterChannel = 0;
    private const byte PythonNotePersonalChannel = 1;
    private const string CenteredRedTextPrefix = "|cFFFF0000";
    private const string CenteredGreenTextPrefix = "|cFF00FF00";
    private const string CenteredTextColorReset = "|cFFFFFFFF";
    internal const int CenteredRedAnnouncementMaximumTextLength =
        CenteredAnnouncementMaximumTextLength - 20;

    /// <summary>
    /// Centred proclamation rendered in green. Same channel and formatter as
    /// <see cref="CenteredRedAnnouncement"/>, only the ARGB markup differs.
    /// </summary>
    public static byte[] CenteredGreenAnnouncement(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > CenteredRedAnnouncementMaximumTextLength ||
            message.Any(static character => character is < ' ' or > '~') ||
            message.Contains("|c", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "Native coloured announcements require at most 106 printable ASCII characters without color markup.");
        }

        return CenteredAnnouncement(
            CenteredGreenTextPrefix + message + CenteredTextColorReset);
    }

    /// <summary>
    /// Uses the stock proclamation renderer's ARGB text markup. The message
    /// is plain printable ASCII; the reset keeps the next notice's color intact.
    /// </summary>
    public static byte[] CenteredRedAnnouncement(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > CenteredRedAnnouncementMaximumTextLength ||
            message.Any(static character => character is < ' ' or > '~') ||
            message.Contains("|c", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "Native red announcements require at most 106 printable ASCII characters without color markup.");
        }

        return CenteredAnnouncement(
            CenteredRedTextPrefix + message + CenteredTextColorReset);
    }

    /// <summary>Direct text in the same native game log as daily item gains, without changing the bag or opening a dialogue.</summary>
    public static byte[] PersonalGameLog(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Any(static character => character is < ' ' or > '~'))
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "Native game logs require printable ASCII text.");
        }

        var packet = CenteredAnnouncement(message);
        // SrvMsg.lua CHANNEL_PRESONAL calls AddPersonalMessage_UTF8(text,6,1).
        packet[8] = PythonNotePersonalChannel;
        return packet;
    }

    /// <summary>
    /// Uses the stock client's direct-text announcement formatter. Its two
    /// fixed strings are concatenated before the center-screen proclamation
    /// is rendered, so splitting here does not alter the visible text.
    /// </summary>
    public static byte[] CenteredAnnouncement(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Any(static character => character > sbyte.MaxValue) ||
            message.Length > CenteredAnnouncementMaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "Native centered announcements require at most 126 ASCII bytes.");
        }

        var packet = ComposeNote(
            PythonNoteDirectTextType,
            PythonNoteCenterChannel,
            message[..Math.Min(message.Length, PythonNoteTextPartLength)],
            message[Math.Min(message.Length, PythonNoteTextPartLength)..]);
        return packet;
    }

    /// <summary>
    /// The activity's donation proclamation: the note type its own client
    /// script composes the message from, on the centre-screen channel, with the
    /// donor in the native name field and the message halves in the note field.
    /// </summary>
    /// <remarks>
    /// The shipped client never receives the Chinese text of this announcement.
    /// <c>SrvMsg.lua</c> declares <c>SrvMsg_NOTE_181 = 33</c> and composes that
    /// type as <c>name .. SrvMsg_Lelantine_msg[note[0]] .. note[2] ..
    /// SrvMsg_Lelantine_msg[note[1]]</c>, so the frame carries the donor's name
    /// and an ASCII note such as <c>51070#51090#110</c>; the client draws
    /// "「名字」捐献犬宝宝宠物蛋，斯巴达阵营获得110积分！" from its own text
    /// table. Capture evidence for the frame's shape is the note channel itself
    /// (<c>S2C 10038 type=… channel=0 name=… note=…</c>, 916 captured frames).
    /// </remarks>
    public static byte[] LelantineDonationBroadcast(
        string donorName,
        bool athenian,
        long points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(donorName);
        var note = LelantineFarmProtocol.BuildDonationBroadcastNote(
            athenian,
            points);
        return ComposeNote(
            LelantineFarmProtocol.DonationBroadcastNoteType,
            checked((byte)LelantineFarmProtocol.DonationBroadcastChannel),
            donorName,
            note);
    }

    /// <summary>
    /// <c>MSG_PYTHON_NOTE</c> on the personal channel: the stock client's own
    /// lower-right log line, carrying text in the client's ANSI code page.
    /// </summary>
    /// <remarks>
    /// <see cref="PersonalGameLog"/> is the ASCII face of this same frame. The
    /// guild window's feedback has to print the client's <em>own</em> wording, and
    /// those texts live in the client's tables in the player's language, so the
    /// bytes are carried verbatim rather than transliterated: on this frame the
    /// client converts the payload with its ANSI code page before the Lua side
    /// prints it (<c>SrvMsg.lua</c>, <c>CHANNEL_PRESONAL</c> ->
    /// <c>GameAPI:AddPersonalMessage_UTF8</c>, whose channel comment reads
    /// 个人右下). The caller is responsible for the code page: the guild texts
    /// below are the client's own bytes, taken from its shipped table.
    /// </remarks>
    public static byte[] PersonalNotice(ReadOnlySpan<byte> encodedText)
    {
        if (encodedText.Length == 0 ||
            encodedText.Length > CenteredAnnouncementMaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodedText),
                $"Native personal notices carry 1..{CenteredAnnouncementMaximumTextLength} encoded bytes.");
        }

        var split = Math.Min(encodedText.Length, PythonNoteTextPartLength);
        return ComposeNoteBytes(
            PythonNoteDirectTextType,
            PythonNotePersonalChannel,
            encodedText[..split],
            encodedText[split..]);
    }

    /// <summary>
    /// One composed note whose two fixed fields are raw encoded bytes rather than
    /// ASCII, so a text the client already owns can be sent without changing it.
    /// </summary>
    private static byte[] ComposeNoteBytes(
        int noteType,
        byte channel,
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second)
    {
        if (first.Length >= PythonNoteFieldLength ||
            second.Length >= PythonNoteFieldLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(first),
                "Native composed notes carry at most 63 bytes in each of their " +
                "two fields.");
        }

        var packet = new byte[PythonNotePacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)PythonNotePacketLength));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PythonNote);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            noteType);
        packet[8] = channel;
        first.CopyTo(packet.AsSpan(9));
        second.CopyTo(packet.AsSpan(73));
        return packet;
    }

    /// <summary>
    /// One composed note: the type the client switches on, the channel it draws
    /// on, and the two fixed 64-byte fields it reads as the name and the note.
    /// </summary>
    private static byte[] ComposeNote(
        int noteType,
        byte channel,
        string name,
        string note)
    {
        if (name.Length >= PythonNoteFieldLength ||
            note.Length >= PythonNoteFieldLength ||
            name.Any(static character => character is < ' ' or > '~') ||
            note.Any(static character => character is < ' ' or > '~'))
        {
            throw new ArgumentOutOfRangeException(
                nameof(note),
                "Native composed notes carry at most 63 printable ASCII bytes " +
                "in each of their two fields.");
        }

        var packet = new byte[PythonNotePacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)PythonNotePacketLength));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PythonNote);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            noteType);
        packet[8] = channel;
        PacketText.WriteFixedAscii(
            packet.AsSpan(9, PythonNoteFieldLength),
            name);
        PacketText.WriteFixedAscii(
            packet.AsSpan(73, PythonNoteFieldLength),
            note);
        return packet;
    }
}
