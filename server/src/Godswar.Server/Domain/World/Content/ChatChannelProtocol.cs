using System.Buffers.Binary;

namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// What one <c>Talk</c> (10035) frame is addressed to.
/// </summary>
internal enum ChatAudience : byte
{
    /// <summary>Whoever is in the sender's own world instance. The stock "nearby" chat.</summary>
    Nearby = 0,
    /// <summary>Every character this process serves, whatever map they are on.</summary>
    World = 1,
    /// <summary>Characters of the sender's own camp.</summary>
    Faction = 2,
    /// <summary>Members of the sender's guild.</summary>
    Guild = 3,
    /// <summary>The sender's own party.</summary>
    Party = 4,
    /// <summary>One named character, wherever he is.</summary>
    Private = 5
}

/// <summary>
/// The stock client's chat frame: which channel a <c>Talk</c> (10035) frame was
/// sent on, and the text half of it.
/// </summary>
/// <remarks>
/// The layout is the 2008 server's <c>MSG_TALK</c> (Common/MsgDefine.h) and was
/// re-read from the client's own receive routine (GodsWar.exe 0x5351bf, the same
/// exe in clients 1.4.9 and 1.5.0) on 2026-11-12:
/// <code>
/// +0  u32  speaker's object id
/// +4  u32  text byte count, including the two-byte terminator
/// +8  u8   channel type          (ucType)
/// +9  u8   speaker name length   (ucResPlayerNameSize, UTF-16 units)
/// +10 u8   target name length    (ucAimPlayerNameSize, UTF-16 units; 0 off the private channel)
/// +11 u8   chat bubble style     (ucChatBubbleSytle)
/// +12      UTF-16LE text: [target name][speaker name][message]
/// </code>
/// (Offsets are inside the frame's payload, i.e. after its 4-byte length/opcode
/// header.) The client cuts the text by the two length bytes and by nothing else:
/// <c>target = text[0..aim]</c>, <c>speaker = text[aim..aim+res]</c>,
/// <c>message = text[aim+res..]</c>. The colon the client writes after its own name
/// is simply the first character of the message. What it then draws depends on the
/// type byte:
/// <code>
/// 0x01 private, received   speaker + Message.dat "SaytoU" + message   ("the admin Say to you: oi")
/// 0x83 private, own copy   Message.dat "SentTo" + target + message    (" Sent to-> a player: oi")
/// 0x02 team, 0x04 guild, 0x08 camp, 0x10 local, 0x82 world: channel tag + speaker + message
/// </code>
/// The client never draws a line for a private message it sent; it waits for the
/// server's 0x83 copy. Frames the operator captured on 2026-10-01 (character
/// <c>test</c>): <c>0x402</c> party, <c>0x404</c> guild, <c>0x408</c> faction,
/// <c>0x410</c> nearby, <c>0x482</c> world (all "test:..." with a 4 in the speaker
/// length byte), <c>0x40401</c> private to 12e1 (<c>12e1test:nihao</c>) and
/// <c>0x60401</c> private to 12v312 (<c>12v312test:haha</c>). The last two differ in
/// the target length byte, 4 and 6.
/// </remarks>
internal static class ChatChannelProtocol
{
    /// <summary>Length of the payload every chat frame carries before its text.</summary>
    public const int PayloadHeaderBytes = 12;

    /// <summary>Type byte of a private message as its recipient receives it.</summary>
    public const byte PrivateType = 0x01;

    /// <summary>Type byte of the party (team) channel.</summary>
    public const byte PartyType = 0x02;

    /// <summary>Type byte of the guild channel.</summary>
    public const byte GuildType = 0x04;

    /// <summary>Type byte of the faction (camp) channel.</summary>
    public const byte FactionType = 0x08;

    /// <summary>Type byte of the nearby (local) channel.</summary>
    public const byte NearbyType = 0x10;

    /// <summary>Type byte of the world channel.</summary>
    public const byte WorldType = 0x82;

    /// <summary>
    /// Type byte of the copy of a private message its sender gets back: the client
    /// draws it as "Sent to-&gt; target message". Only the server writes it.
    /// </summary>
    public const byte PrivateSentType = 0x83;

    /// <summary>The longest name a length byte can describe.</summary>
    public const int MaximumNameLength = byte.MaxValue;

    /// <summary>The channel type byte of a channel word.</summary>
    public static byte ReadType(uint channelWord) => (byte)channelWord;

    /// <summary>The speaker name length a channel word declares, in UTF-16 units.</summary>
    public static int ReadSpeakerNameLength(uint channelWord) =>
        (int)((channelWord >> 8) & 0xFF);

    /// <summary>The target name length a channel word declares, in UTF-16 units.</summary>
    public static int ReadTargetNameLength(uint channelWord) =>
        (int)((channelWord >> 16) & 0xFF);

    /// <summary>
    /// Reads the channel word and the text out of a chat frame's payload.
    /// </summary>
    /// <remarks>
    /// The text is the client's own bytes: the client already prefixes the
    /// speaker's name (<c>test:nihao</c>), so this server never composes it again.
    /// </remarks>
    public static bool TryRead(
        ReadOnlySpan<byte> payload,
        out uint channelWord,
        out string text)
    {
        channelWord = 0;
        text = string.Empty;
        if (payload.Length < PayloadHeaderBytes)
        {
            return false;
        }

        var lengthWithTerminator = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(sizeof(uint), sizeof(uint)));
        if (lengthWithTerminator < sizeof(ushort) ||
            (lengthWithTerminator & 1) != 0 ||
            lengthWithTerminator - sizeof(ushort) >
                payload.Length - PayloadHeaderBytes)
        {
            return false;
        }

        var textLength = checked((int)lengthWithTerminator - sizeof(ushort));
        var decoded = System.Text.Encoding.Unicode.GetString(
            payload.Slice(PayloadHeaderBytes, textLength));
        text = decoded.TrimEnd('\0');
        if (text.Length == 0)
        {
            return false;
        }

        channelWord = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(sizeof(uint) * 2, sizeof(uint)));
        return true;
    }

    /// <summary>
    /// Which audience a channel word addresses, by its type byte alone.
    /// </summary>
    /// <remarks>
    /// The two name length bytes play no part. Until 2026-11-12 a private message
    /// was recognised by bits 0x40000 and 0x400, which are bit 2 of the target's and
    /// of the speaker's name length: a whisper to or from a name of 1-3, 8-11 or
    /// 16-19 characters was taken for a nearby line and shown to the whole map.
    /// <para>
    /// A word this build does not recognise falls back to
    /// <see cref="ChatAudience.Nearby"/>, which is the behaviour that shipped
    /// before channels existed: an unknown channel can never swallow a message or
    /// send it further than the sender's own map.
    /// </para>
    /// </remarks>
    public static ChatAudience ResolveAudience(uint channelWord) =>
        ReadType(channelWord) switch
        {
            PrivateType => ChatAudience.Private,
            WorldType => ChatAudience.World,
            PartyType => ChatAudience.Party,
            GuildType => ChatAudience.Guild,
            FactionType => ChatAudience.Faction,
            _ => ChatAudience.Nearby
        };

    /// <summary>
    /// The text of a private message as the server relays it, the same for both
    /// sides: <c>[target][speaker]: message</c>.
    /// </summary>
    /// <remarks>
    /// This is the shape the client itself sends (<c>12e1test:nihao</c>) with one
    /// space added after the colon, so the two lines read
    /// "the admin Say to you: oi" and " Sent to-&gt; a player: oi". The names are written
    /// whole; <see cref="BuildPrivateWord"/> tells the client where each one ends.
    /// Measured in the real client on 2026-11-12 with name pairs of 3 to 16
    /// characters (screens: Development/w5/g-whisper/screens).
    /// </remarks>
    public static string BuildPrivateBody(
        string targetName,
        string speakerName,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerName);
        ArgumentNullException.ThrowIfNull(message);
        if (targetName.Length > MaximumNameLength ||
            speakerName.Length > MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetName),
                "A chat frame describes each name's length in one byte.");
        }

        return targetName + speakerName + ": " + message.TrimStart();
    }

    /// <summary>
    /// The channel word that goes with <see cref="BuildPrivateBody"/>: the given
    /// type, the two names' real lengths, and the bubble style byte of the word the
    /// speaker's frame carried.
    /// </summary>
    /// <param name="type">
    /// <see cref="PrivateType"/> for the recipient's copy,
    /// <see cref="PrivateSentType"/> for the speaker's.
    /// </param>
    public static uint BuildPrivateWord(
        byte type,
        string targetName,
        string speakerName,
        uint sourceWord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerName);
        if (targetName.Length > MaximumNameLength ||
            speakerName.Length > MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetName),
                "A chat frame describes each name's length in one byte.");
        }

        return (sourceWord & 0xFF00_0000u) |
            ((uint)targetName.Length << 16) |
            ((uint)speakerName.Length << 8) |
            type;
    }

    /// <summary>
    /// Splits a private message's text into its recipient and its message, by the
    /// name lengths the client wrote in the channel word.
    /// </summary>
    /// <remarks>
    /// This is exact for every name pair: a recipient whose name starts or ends
    /// with the speaker's, or a name or message with a colon in it, cannot move the
    /// cut. The speaker segment must be the session's own character name, and the
    /// message must open with the colon the client writes. A frame whose length
    /// bytes do not describe its text that way is read by the older rule (the
    /// first colon), so nothing that was delivered before is refused now.
    /// </remarks>
    public static bool TryReadPrivateRecipient(
        uint channelWord,
        string text,
        string speakerName,
        out string recipientName,
        out string message)
    {
        recipientName = string.Empty;
        message = string.Empty;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(speakerName))
        {
            return false;
        }

        var targetLength = ReadTargetNameLength(channelWord);
        var speakerLength = ReadSpeakerNameLength(channelWord);
        var namesLength = targetLength + speakerLength;
        if (targetLength > 0 &&
            speakerLength == speakerName.Length &&
            text.Length > namesLength &&
            text[namesLength] == ':' &&
            string.Compare(
                text,
                targetLength,
                speakerName,
                0,
                speakerLength,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            recipientName = text[..targetLength].Trim();
            message = text[(namesLength + 1)..];
            return recipientName.Length > 0;
        }

        return TryReadPrivateRecipient(
            text,
            speakerName,
            out recipientName,
            out message);
    }

    /// <summary>
    /// Splits a private message's text into its recipient and its message without
    /// the channel word: the fallback of the overload that has it.
    /// </summary>
    /// <remarks>
    /// The captured text is <c>&lt;recipient&gt;&lt;speaker&gt;:&lt;message&gt;</c>
    /// (<c>12e1test:nihao</c>), so the colon separates the name pair from what was
    /// typed, and the recipient is the prefix that remains once the speaker's own
    /// name is taken off the front. Requiring that prefix keeps this from treating
    /// an ordinary sentence containing a colon as a private message.
    /// </remarks>
    public static bool TryReadPrivateRecipient(
        string text,
        string speakerName,
        out string recipientName,
        out string message)
    {
        recipientName = string.Empty;
        message = string.Empty;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(speakerName))
        {
            return false;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        var names = text[..colon];
        if (names.Length <= speakerName.Length ||
            !names.EndsWith(speakerName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        recipientName = names[..^speakerName.Length].Trim();
        message = text[(colon + 1)..];
        return recipientName.Length > 0;
    }
}
