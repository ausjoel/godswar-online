using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The guild window's proclamation (placard) edit, read off the client.
/// </summary>
/// <remarks>
/// Measured on 2026-09-22 by typing a proclamation into the guild window and
/// confirming it:
///
/// <code>
/// C2S 10150 (MSG_CONSORTIA_TEXT), 136-byte frame, 132-byte payload
///   +0     u32        the editing character's object id (0x1448 = 5192)
///   +4     char[128]  the text, ASCII, NUL terminated ("gonggao")
/// </code>
///
/// The client's own case for this opcode resolves the text at <c>body+4</c> and
/// hands it to <c>0x4a8830</c>, the same setter the placard update
/// (<c>10141</c>) uses with <c>body+0</c> - so the answer to this request is the
/// placard message, not a copy of the request.
/// </remarks>
internal static class GuildTextProtocol
{
    /// <summary>The request's frame: object id plus the full text field.</summary>
    public const int PayloadLength = 4 + TextLength;

    /// <summary>Where the text starts and how much room the client gives it.</summary>
    private const int TextOffset = 4;
    private const int TextLength = 128;

    /// <summary>
    /// The longest text the guild window can show: the proclamation field of the
    /// base info message is 64 bytes including its terminator.
    /// </summary>
    public const int MaximumStoredLength = 63;

    public static bool TryReadTextRequest(GamePacket packet, out string text)
    {
        text = string.Empty;
        var payload = packet.Payload;
        if (payload.Length < PayloadLength)
        {
            return false;
        }

        var field = payload.Slice(TextOffset, TextLength);
        var terminator = field.IndexOf((byte)0);
        if (terminator >= 0)
        {
            field = field[..terminator];
        }

        text = System.Text.Encoding.ASCII.GetString(field).Trim();
        return text.Length > 0;
    }
}
