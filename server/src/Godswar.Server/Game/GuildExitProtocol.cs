using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The guild window's leave action, read off the client.
/// </summary>
/// <remarks>
/// Measured on 2026-09-22 by pressing G and clicking leave as the only member of
/// the guild named "test":
///
/// <code>
/// C2S 10149 (MSG_CONSORTIA_EXIT), 70-byte frame, 66-byte payload
///   +0    char[64]  the leaving character's name, ASCII, zero padded ("test")
///   +64   2 bytes   zero
/// </code>
///
/// That capture cannot tell the character's name from the guild's, because both
/// were "test". The server does not need it to: a leave is only ever honoured for
/// the session that sent it, and the session already names the character. The
/// name is checked against it only to reject a request that names someone else,
/// which is the window's other user of this message (removing a member), still
/// without an established layout.
///
/// The player's own answer is not read from a capture either: the reference
/// server was never reached with a leave. It was read out of the client instead.
/// The case at <c>0x4ed4df</c> takes <c>body+0</c> as a name, resolves it and
/// branches on the result - it reads no other field of the message.
/// </remarks>
internal static class GuildExitProtocol
{
    /// <summary>How long the client's name field is.</summary>
    private const int NameLength = 64;

    /// <summary>
    /// Reads the name the leave request carries.
    /// </summary>
    public static bool TryReadExitRequest(GamePacket packet, out string name)
    {
        name = string.Empty;
        var payload = packet.Payload;
        if (payload.Length == 0)
        {
            return false;
        }

        var field = payload.Slice(
            0,
            Math.Min(NameLength, payload.Length));
        var terminator = field.IndexOf((byte)0);
        if (terminator >= 0)
        {
            field = field[..terminator];
        }

        name = System.Text.Encoding.ASCII.GetString(field).Trim();
        return name.Length > 0;
    }
}
