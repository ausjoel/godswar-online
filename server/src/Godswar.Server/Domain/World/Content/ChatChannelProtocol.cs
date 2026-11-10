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
/// The layout is read off the client's own frames, captured 2026-10-01 on the local
/// server while the operator sent one message per channel (character <c>test</c>,
/// character id 2):
/// <code>
/// +0  u32  speaker's object id   0x1448 on every captured frame
/// +4  u32  text byte count, including the two-byte terminator
/// +8  u32  channel word
/// +12      UTF-16LE text
/// </code>
/// (Both offsets are inside the frame's payload, i.e. after its 4-byte
/// length/opcode header.) The channel word is a bit field. Bits 1..4 are the chat
/// window's channel selector, exactly one at a time; bit 7 is the realm-wide flag
/// the client adds when the message is sent on the world channel; and a private
/// message carries <see cref="PrivateFlag"/> with <see cref="PrivateSelector"/> in
/// its low bits, with the recipient's name opening the text. Every value below is a
/// captured frame, not an inference:
/// <code>
/// 0x402 party    test:damson
/// 0x404 guild    test:n2
/// 0x408 faction  test:cherry
/// 0x410 nearby   test:appl
/// 0x482 world    test:appl and test:banana (same word, two different sends)
/// 0x40401 private to 12e1   "12e1test:nihao"
/// 0x60401 private to 12v312 "12v312test:haha"
/// </code>
/// The two private frames differ by <c>0x20000</c>, whose meaning is NOT
/// established: nothing here depends on it, and only the flag bit plus the
/// selector, plus the presence of a recipient name in the text, decide that a frame
/// is private.
/// </remarks>
internal static class ChatChannelProtocol
{
    /// <summary>Length of the payload every chat frame carries before its text.</summary>
    public const int PayloadHeaderBytes = 12;

    /// <summary>The realm-wide flag the client sets for the world channel.</summary>
    public const uint WorldFlag = 0x0000_0080;

    /// <summary>
    /// The flag a private message carries. Its low bits are
    /// <see cref="PrivateSelector"/> as well, so a private message is recognised by
    /// both: a word with only this bit set is not one this build knows.
    /// </summary>
    public const uint PrivateFlag = 0x0004_0000;

    /// <summary>
    /// The selector bits a private message carries beside its flag: both captured
    /// private frames end in <c>0x401</c>.
    /// </summary>
    public const uint PrivateSelector = 0x0000_0401;

    private const uint ChannelMask = 0x0000_001E;
    private const uint NearbyBit = 0x0000_0010;
    private const uint PartyBit = 0x0000_0002;
    private const uint GuildBit = 0x0000_0004;
    private const uint FactionBit = 0x0000_0008;

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
    /// Which audience a captured channel word addresses.
    /// </summary>
    /// <remarks>
    /// A word this build does not recognise falls back to
    /// <see cref="ChatAudience.Nearby"/>, which is the behaviour that shipped
    /// before channels existed: an unknown channel can never swallow a message or
    /// send it further than the sender's own map.
    /// </remarks>
    public static ChatAudience ResolveAudience(uint channelWord) =>
        (channelWord & PrivateFlag) != 0 &&
        (channelWord & PrivateSelector) == PrivateSelector
            ? ChatAudience.Private
            : (channelWord & WorldFlag) != 0
                ? ChatAudience.World
                : (channelWord & ChannelMask) switch
                {
                    PartyBit => ChatAudience.Party,
                    GuildBit => ChatAudience.Guild,
                    FactionBit => ChatAudience.Faction,
                    NearbyBit => ChatAudience.Nearby,
                    _ => ChatAudience.Nearby
                };

    /// <summary>
    /// The number of UTF-16 characters the recipient's client reads as the private
    /// line's second name - the one it renders as "对&lt;name&gt;说:".
    /// </summary>
    /// <remarks>
    /// Measured across the operator's live tests on 2026-10-01. The client's line is
    /// <c>[text 0..4] + "对" + [text 4..8] + "说:" + [text 10..]</c>, which every
    /// observation fits:
    /// <list type="bullet">
    /// <item><c>12e1test:nihao</c> (the captured frame) rendered as
    /// <c>test对你说:nihao</c> - so the visible name is <c>[4..8]</c>, and the
    /// digits after character ten are the message;</item>
    /// <item><c>test:nihao</c> rendered as <c>test对你说:</c> with no message:
    /// the message starts at character ten and that text ends there;</item>
    /// <item>the sentence sent to test this formula - <c>12e1对test说:hello</c> -
    /// rendered as <c>对tes对你说t说:hello</c>, i.e. <c>[0..4]="对tes"</c>,
    /// <c>[4..8]="t"</c> as the name, and <c>[10..]="t说:hello"</c> as the
    /// message.</item>
    /// </list>
    /// </remarks>
    public const int PrivateVisibleNameLength = 4;

    /// <summary>
    /// The number of UTF-16 characters of a private message's text that precede its
    /// message.
    /// </summary>
    public const int PrivateBodyPrefixLength = 10;

    /// <summary>
    /// Builds the text of one side of a private conversation.
    /// </summary>
    /// <param name="leadName">
    /// The name whose four characters occupy the text's first slot. The client does
    /// not render them; they only hold the layout.
    /// </param>
    /// <param name="visibleName">
    /// The name the client renders in the line, i.e. the other party: the speaker on
    /// the recipient's copy, the recipient on the speaker's copy.
    /// </param>
    /// <param name="message">What was typed.</param>
    /// <remarks>
    /// Two live measurements pin the layout, and both come from the operator reading
    /// the line his own client drew:
    /// <list type="bullet">
    /// <item>a body of <c>test:nihao</c> drew nothing after the colon, so the message
    /// starts at character nine;</item>
    /// <item>a body of <c>test12e1说:hello</c> drew
    /// <c>test对12e1说:说:hello</c>, i.e. the client wrote its own <c>说:</c> and then
    /// repeated what it found from character nine. So the client's line is
    /// <c>[0..4] + 对 + [4..8] + 说: + [9..]</c> and the colon at character eight is
    /// the one its <c>说:</c> replaces.</item>
    /// </list>
    /// The body is therefore <c>&lt;lead&gt;&lt;visible&gt;:&lt;message&gt;</c> with
    /// both names padded or truncated to four characters, never the word
    /// <c>说</c>.
    /// </remarks>
    public static string BuildPrivateBody(
        string leadName,
        string visibleName,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leadName);
        ArgumentException.ThrowIfNullOrWhiteSpace(visibleName);
        ArgumentNullException.ThrowIfNull(message);
        return Slot(leadName) + Slot(visibleName) + ":" + message;
    }

    private static string Slot(string name) =>
        name.Length > PrivateVisibleNameLength
            ? name[..PrivateVisibleNameLength]
            : name.PadRight(PrivateVisibleNameLength);

    /// <summary>
    /// Splits a private message's text into its recipient and its message.
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
