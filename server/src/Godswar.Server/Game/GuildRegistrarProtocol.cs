using System.Buffers.Binary;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The guild registrar's create conversation, read off the client.
/// </summary>
/// <remarks>
/// The registrar himself is opened with flags <c>0x80</c> (see
/// <c>GameClientHandler.NpcDialogOpen.cs</c>); the create itself does not go
/// through the dialog window at all. Measured on 2026-09-21 by clicking create
/// with a character the client accepted:
///
/// <code>
/// C2S 10136, 460-byte payload
///   +0    u32        creator object id (0x1448 = 5192)
///   +4    char[64]   guild name, ASCII, zero padded ("test")
///   +68   char[64]   the same name again
///   +132  256 bytes  zero
///   +388  u32        member record: creator object id
///   +396  u32        member duty = 6, which Consortia.dat maps to "guild lord"
///   +400  char[64]   member name
///   +464  ...
/// </code>
///
/// The client then waits for the server's answer; nothing else follows on its
/// own. The answer's opcode and layout are not captured yet: the reference server
/// refuses the attempt locally on a character below level 30 ("you need level 30"
/// is the client's own text, and a refused attempt sends nothing), so the capture
/// has the request only.
/// </remarks>
internal static class GuildRegistrarProtocol
{
    /// <summary>The create request the client sends.</summary>
    public const ushort CreateRequest = 10136;

    /// <summary>
    /// The level the client's own refusal text names (<c>ERROR_035B</c>, "you need
    /// level 30 to found a guild") and the registrar's description repeats.
    /// </summary>
    public const int MinimumLevel = 30;

    /// <summary>
    /// Where the guild name starts and how long the client's name field is.
    /// </summary>
    private const int NameOffset = 4;
    private const int NameLength = 64;

    /// <summary>Where the creator's own object id is.</summary>
    private const int CreatorOffset = 0;

    public static bool TryReadCreateRequest(
        GamePacket packet,
        out uint creatorId,
        out string name)
    {
        creatorId = 0;
        name = string.Empty;
        var payload = packet.Payload;
        if (payload.Length < NameOffset + 1)
        {
            return false;
        }

        creatorId = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(CreatorOffset, sizeof(uint)));
        var field = payload.Slice(
            NameOffset,
            Math.Min(NameLength, payload.Length - NameOffset));
        var end = field.IndexOf((byte)0);
        if (end >= 0)
        {
            field = field[..end];
        }

        name = System.Text.Encoding.ASCII.GetString(field).Trim();
        return name.Length > 0;
    }

    /// <summary>
    /// Echoes the request back as the answer, which is the shape the client's own
    /// acknowledgement opcodes use (10067 travels both ways). This is a probe: the
    /// captured reference attempt never reached the server, so the answer's shape
    /// is being established by watching what the client does with it.
    /// </summary>
    public static byte[] CreateProbeResponse(ReadOnlySpan<byte> requestPayload)
    {
        var packet = new byte[sizeof(ushort) * 2 + requestPayload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, sizeof(ushort)),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(sizeof(ushort), sizeof(ushort)),
            CreateRequest);
        requestPayload.CopyTo(packet.AsSpan(sizeof(ushort) * 2));
        return packet;
    }
}
