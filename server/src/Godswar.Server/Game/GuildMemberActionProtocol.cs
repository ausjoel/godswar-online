using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The guild window's member actions: the duty change and the removal.
/// </summary>
/// <remarks>
/// Both were read off the client on 2026-09-22 by using them against a two-member
/// guild:
///
/// <code>
/// C2S 10151 (MSG_CONSORTIA_DUTY), 70-byte frame, 66-byte payload
///   +0x00  char[32]  the acting character's name ("test")
///   +0x20  char[32]  the member's name ("Officer")
///   +0x40  u8        zero in every capture; meaning unproven
///   +0x41  u8        the member's new duty (2 = 会员, promoting from 1)
///
/// C2S 10152 (MSG_CONSORTIA_MEMBER_DEL), 40-byte frame, 36-byte payload
///   +0x00  u32       the acting character's object id (0x1448 = 5192)
///   +0x04  char[32]  the removed member's name ("Officer")
/// </code>
///
/// Neither request carries the guild: the session names the actor, and the actor
/// names the guild.
/// </remarks>
internal static class GuildMemberActionProtocol
{
    /// <summary>The duty change the client sends.</summary>
    public const ushort DutyRequest = Opcodes.ConsortiaDuty;

    /// <summary>The member removal the client sends.</summary>
    public const ushort MemberDelRequest = Opcodes.ConsortiaMemberDel;

    /// <summary>
    /// The duty a member needs before the window's management actions are
    /// allowed: <c>Consortia_Job.ini</c> numbers 4 as 理事, and the client's own
    /// refusal text is <c>ERROR_03C2 理事以下职位没有相应权限</c>.
    /// </summary>
    public const byte ManagementDuty = 4;

    private const int NameLength = 32;

    /// <summary>
    /// Where the member's name starts: <c>0x21</c>, not <c>0x20</c>.
    /// </summary>
    /// <remarks>
    /// Measured the hard way. The captured payload reads
    /// <c>+0x20 = 00</c> followed by <c>"Officer"</c>, which is what the client's
    /// own case for this opcode had already said: it resolves the member name at
    /// <c>body+0x21</c>. Reading from <c>0x20</c> yields an empty name and the
    /// request is refused, which is exactly what the first implementation did.
    /// </remarks>
    private const int DutyMemberOffset = 0x21;

    private const int DutyValueOffset = 0x41;

    private const int MemberDelNameOffset = 0x04;

    public static bool TryReadDutyRequest(
        GamePacket packet,
        out string actorName,
        out string memberName,
        out byte duty)
    {
        actorName = string.Empty;
        memberName = string.Empty;
        duty = 0;
        var payload = packet.Payload;
        if (payload.Length <= DutyValueOffset)
        {
            return false;
        }

        actorName = ReadName(payload, 0);
        memberName = ReadName(payload, DutyMemberOffset);
        duty = payload[DutyValueOffset];
        return actorName.Length > 0 && memberName.Length > 0 && duty > 0;
    }

    public static bool TryReadMemberDelRequest(
        GamePacket packet,
        out uint objectId,
        out string memberName)
    {
        objectId = 0;
        memberName = string.Empty;
        var payload = packet.Payload;
        if (payload.Length < MemberDelNameOffset + 1)
        {
            return false;
        }

        objectId = System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(payload);
        memberName = ReadName(payload, MemberDelNameOffset);
        return memberName.Length > 0;
    }

    private static string ReadName(ReadOnlySpan<byte> payload, int offset)
    {
        if (offset >= payload.Length)
        {
            return string.Empty;
        }

        var field = payload.Slice(
            offset,
            Math.Min(NameLength, payload.Length - offset));
        var terminator = field.IndexOf((byte)0);
        if (terminator >= 0)
        {
            field = field[..terminator];
        }

        return System.Text.Encoding.ASCII.GetString(field).Trim();
    }
}
