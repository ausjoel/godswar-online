using System.Buffers.Binary;
using Godswar.Server.Application.Guilds;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

/// <summary>
/// The consortia messages the stock client renders its guild window from.
/// </summary>
/// <remarks>
/// The layouts were recovered from the client itself. Its receive dispatcher
/// (<c>0x4ea3d0</c>) selects a case per opcode through the byte table at
/// <c>0x4ee900</c>, and the case body plus the player method it calls name every
/// field they read:
/// <list type="bullet">
/// <item><c>10137</c> create response - the success branch reads the result at
/// body+0, the name at body+4, the level at body+0x64 and the outcome kind at
/// body+0x1CC.</item>
/// <item><c>10138</c> base info - <c>SetConsortiaInfo</c> reads the name at
/// body+0, the level at body+0x40, the window's numbers at body+0x44/0x48/0x4C
/// and body+0x154/0x158/0x15C, the proclamation at body+0x50 and the count at
/// body+0x164.</item>
/// <item><c>10143</c> member list - the handler reads a count at body+4 and
/// 52-byte records from body+8, twenty of them before its flag at body+0x418.
/// Each record holds the name at +0, the level at +0x20, the duty at +0x22 and
/// the profession at +0x24.</item>
/// </list>
/// Only fields the client demonstrably reads are written; every other byte of
/// these fixed-size messages stays zero. The meanings proven for each written
/// offset are recorded in <c>docs/工会系统技术文档.md</c>.
/// </remarks>
internal static partial class PacketBuilder
{
    private const int GuildNameLength = 64;

    private const int GuildProclamationLength = 64;

    private const int GuildMemberRecordLength = 52;

    private const int GuildMemberCapacity = 20;

    private const int GuildMemberNameLength = 32;

    /// <summary>One (type, level) pair of a building array entry.</summary>
    private const int BuildingEntryLength = 8;

    /// <summary>
    /// The measured width of a leave message: the name field plus the two zero
    /// bytes the captured request carries after it.
    /// </summary>
    private const int GuildExitPayloadLength = 66;

    /// <summary>
    /// The founder's answer to <c>10136</c>:
    /// <c>MSG_CONSORTIA_CREATE_RESPONSE</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="creatorObjectId"/> is the first field because the client
    /// resolves the message against it, not against a result code: its case for
    /// this opcode calls <c>0x4a6e70(body+0)</c>, which walks the client's object
    /// list and returns the entry whose id matches, and the success branch then
    /// writes the guild name into that object (<c>+0xa24</c>). An id that matches
    /// nothing returns null and the client faults at <c>0x4a80db</c> - measured
    /// three times, at <c>D:\Godswar Origin\Dump\Error.log</c> (2026-09-22
    /// 19:58:13, 20:06:49, 20:13:39, all <c>ESI=0</c>), every one of them a
    /// create, and never once with the field carrying anything but zero.
    ///
    /// The create request carries the requester's object id at its own
    /// <c>body+0</c> (measured: <c>0x1448</c> = 5192, this server's local player
    /// id), so the answer carries it back.
    /// </remarks>
    public static byte[] GuildCreateResponse(
        GuildSnapshot guild,
        uint creatorObjectId)
    {
        ArgumentNullException.ThrowIfNull(guild);
        var packet = new byte[4 + 0x1CD];
        WriteGuildHeader(packet, Opcodes.ConsortiaCreateResponse);
        var body = packet.AsSpan(4);
        WriteUInt32(body, 0x00, creatorObjectId);
        PacketText.WriteFixedAscii(
            body.Slice(0x04, GuildNameLength + 32),
            guild.Name);
        body[0x64] = guild.Level;
        WriteUInt32(body, 0x68, 0);
        WriteUInt32(body, 0x6C, unchecked((uint)guild.Gold));
        WriteUInt32(body, 0x70, 0);
        PacketText.WriteFixedAscii(
            body.Slice(0x74, GuildProclamationLength),
            guild.Proclamation);
        WriteUInt32(body, 0x178, unchecked((uint)guild.Silver));
        WriteUInt32(body, 0x17C, unchecked((uint)guild.Bijou));
        WriteUInt32(body, 0x180, unchecked((uint)guild.MemberLimit));
        // The create response's building array sits at 0x18C, before its outcome
        // byte at 0x1CC: eight entries fit.
        var buildings = Math.Min(guild.Buildings.Count, 8);
        WriteUInt32(body, 0x184, (uint)buildings);
        for (var index = 0; index < buildings; index++)
        {
            var building = guild.Buildings[index];
            WriteUInt32(
                body,
                0x18C + (index * BuildingEntryLength),
                unchecked((uint)building.BuildingType));
            WriteUInt32(
                body,
                0x190 + (index * BuildingEntryLength),
                building.Level);
        }

        body[0x1CC] = 0;
        return packet;
    }

    /// <summary>
    /// <c>MSG_CONSORTIA_BASE_INFO</c>: the guild record. It also sets the
    /// client's own guild state, so it is what makes the window render for a
    /// character that is already a member.
    /// </summary>
    public static byte[] GuildBaseInfo(GuildSnapshot guild)
    {
        ArgumentNullException.ThrowIfNull(guild);
        var packet = new byte[
            4 + 0x168 + (guild.Buildings.Count * BuildingEntryLength)];
        WriteGuildHeader(packet, Opcodes.ConsortiaBaseInfo);
        var body = packet.AsSpan(4);
        PacketText.WriteFixedAscii(body[..GuildNameLength], guild.Name);
        body[0x40] = guild.Level;
        WriteUInt32(body, 0x44, unchecked((uint)guild.Gold));
        WriteUInt32(body, 0x48, 0);
        WriteUInt32(body, 0x4C, 0);
        PacketText.WriteFixedAscii(
            body.Slice(0x50, GuildProclamationLength),
            guild.Proclamation);
        WriteUInt32(body, 0x154, unchecked((uint)guild.Silver));
        WriteUInt32(body, 0x158, unchecked((uint)guild.Bijou));
        WriteUInt32(body, 0x15C, unchecked((uint)guild.MemberLimit));
        WriteUInt32(body, 0x160, 0);
        WriteBuildings(body, guild, 0x164, 0x168);
        return packet;
    }

    /// <summary>
    /// Writes the guild's building array where this message keeps it.
    /// </summary>
    /// <remarks>
    /// Each entry is a (type, level) pair. The member table is emphatically not
    /// this array: filling it with member records makes the client list them as
    /// buildings, which is how the window came to show garbage rows (measured
    /// 2026-09-21, <c>docs/工会系统技术文档.md</c> §4.1.1).
    /// </remarks>
    private static void WriteBuildings(
        Span<byte> body,
        GuildSnapshot guild,
        int countOffset,
        int arrayOffset)
    {
        var count = guild.Buildings.Count;
        WriteUInt32(body, countOffset, (uint)count);
        for (var index = 0; index < count; index++)
        {
            var building = guild.Buildings[index];
            WriteUInt32(
                body,
                arrayOffset + (index * BuildingEntryLength),
                unchecked((uint)building.BuildingType));
            WriteUInt32(
                body,
                arrayOffset + 4 + (index * BuildingEntryLength),
                building.Level);
        }
    }

    /// <summary>
    /// <c>MSG_CONSORTIA_MEMBER_LIST</c>: up to twenty rows, the most the
    /// client's own message holds before its trailing flag.
    /// </summary>
    public static byte[] GuildMemberList(
        IReadOnlyList<GuildMemberSnapshot> members,
        int selfCharacterId)
    {
        ArgumentNullException.ThrowIfNull(members);
        var packet = new byte[4 + 0x419];
        WriteGuildHeader(packet, Opcodes.ConsortiaMemberList);
        var body = packet.AsSpan(4);
        var count = Math.Min(members.Count, GuildMemberCapacity);
        WriteUInt32(body, 0x00, 0);
        WriteUInt32(body, 0x04, (uint)count);
        for (var index = 0; index < count; index++)
        {
            var member = members[index];
            var record = body.Slice(
                0x08 + (index * GuildMemberRecordLength),
                GuildMemberRecordLength);
            PacketText.WriteFixedAscii(
                record[..GuildMemberNameLength],
                member.Name);
            // +0x20 is the member's own state word. The client reads it with
            // `movsx ecx, word ptr [esi+0x20]` (MEMBER_LIST 0x4edbdf) and its row
            // renderer branches on the value being -1 (`0x540cf4 cmp dword
            // [esi+0x34], -1`, where the row's data comes from this record), so
            // an offline member is written as -1 and an online one as 0.
            // Measured 2026-10-02: while every row carried 0, the client drew an
            // offline fixture as online - which is what this field is for.
            BinaryPrimitives.WriteInt16LittleEndian(
                record[0x20..],
                member.Online ? (short)0 : (short)-1);
            record[0x22] = member.Duty;
            // +0x23 is the member's level as a byte, not a flag: writing 1 here
            // made the row read "level 1" while the character was 140. The
            // client's own level ceiling is 200 (PlayLv ranges), which is why a
            // byte holds it.
            record[0x23] = checked((byte)Math.Clamp((int)member.Level, 0, 200));
            record[0x24] = member.Profession;
            // +0x28/+0x2C are the contribution pair. Writing 2 into +0x2C made
            // the client print it under gold contribution, so +0x2C is the gold
            // contribution slot; +0x28 is the silver one by position and is
            // still unconfirmed. The guild's own contributions exist in
            // guild_members and will be fed here once a contribution flow
            // writes them.
            // The contribution pair. Writing a distinguishable value and reading
            // the live client put the gold column at +0x2C and the silver one at
            // +0x30; +0x28 has no reading yet and stays zero. The values come from
            // the membership row, which the donation transaction writes.
            WriteUInt32(record, 0x28, 0);
            WriteUInt32(
                record,
                0x2C,
                unchecked((uint)member.ContributionGold));
            WriteUInt32(
                record,
                0x30,
                unchecked((uint)member.ContributionSilver));
        }

        // The trailing flag means "this is the whole roster": the client resets
        // the guild record's member container before it adds the rows. Leaving
        // it clear makes every later push append, which is how a repeated
        // answer (login push plus window request) showed one member twice.
        body[0x418] = 1;
        return packet;
    }

    /// <summary>
    /// <c>MSG_CONSORTIA_EXIT</c>: the answer to the window's leave action.
    /// </summary>
    /// <remarks>
    /// The client's case for this opcode (<c>0x4ed4df</c>) reads one field, the
    /// name at body+0, resolves it and branches on whether it was found; it never
    /// touches the rest. The captured request carries that name followed by two
    /// zero bytes, which is the whole width written here. Withheld, the window
    /// does not clear (measured); sent, it does.
    /// </remarks>
    public static byte[] GuildExitNotification(string characterName)
    {
        var packet = new byte[4 + GuildExitPayloadLength];
        WriteGuildHeader(packet, Opcodes.ConsortiaExit);
        PacketText.WriteFixedAscii(
            packet.AsSpan(4, GuildNameLength),
            characterName);
        return packet;
    }

    /// <summary>
    /// <c>MSG_CONSORTIA_UPDATE_PLACARD_INFO</c>: the guild's proclamation, on its
    /// own.
    /// </summary>
    /// <remarks>
    /// The client's case for this opcode (<c>0x4edae7</c>) resolves one field, the
    /// string at <c>body+0</c>, and hands it to the same setter the text request
    /// reaches (<c>0x4a8830</c>); it reads nothing else, and the setter takes a
    /// terminated string rather than a counted one. The text goes out exactly as
    /// it is stored, terminator included, with no padding.
    /// </remarks>
    public static byte[] GuildPlacard(string proclamation)
    {
        ArgumentNullException.ThrowIfNull(proclamation);
        var packet = new byte[4 + proclamation.Length + 1];
        WriteGuildHeader(packet, Opcodes.ConsortiaUpdatePlacardInfo);
        PacketText.WriteFixedAscii(packet.AsSpan(4), proclamation);
        return packet;
    }

    /// <summary>
    /// <c>MSG_CONSORTIA_DUTY</c>: a member's duty, before and after.
    /// </summary>
    /// <remarks>
    /// The client's case for this opcode (<c>0x4ed78f</c>) reads the second name at
    /// <c>body+0x21</c> and a signed duty byte at <c>body+0x41</c>, which fixes the
    /// layout as a one-byte kind, then two 32-byte names, then the duty. The
    /// captured request carries the same two names with the new duty at its own
    /// <c>+0x41</c> and a zero byte at <c>+0x40</c> (measured 2026-09-22: promoting
    /// <c>Officer</c> from 1 sent <c>+0x41 = 2</c>).
    /// </remarks>
    public static byte[] GuildDutyChanged(
        byte kind,
        string actorName,
        string memberName,
        byte duty)
    {
        var packet = new byte[4 + 1 + (GuildMemberNameLength * 2) + 1];
        WriteGuildHeader(packet, Opcodes.ConsortiaDuty);
        var body = packet.AsSpan(4);
        body[0] = kind;
        PacketText.WriteFixedAscii(
            body.Slice(0x01, GuildMemberNameLength),
            actorName);
        PacketText.WriteFixedAscii(
            body.Slice(0x21, GuildMemberNameLength),
            memberName);
        body[0x41] = duty;
        return packet;
    }

    /// <summary>
    /// <c>MSG_CONSORTIA_MEMBER_DEL</c>: one member is no longer in the guild.
    /// </summary>
    /// <remarks>
    /// Measured from the client's case (<c>0x4ed882</c>, name at <c>body+4</c>) and
    /// from the request it answers, whose payload is exactly this shape: the
    /// requester's object id followed by the removed member's name
    /// (36-byte payload, 40-byte frame).
    /// </remarks>
    public static byte[] GuildMemberRemoved(uint objectId, string memberName)
    {
        var packet = new byte[4 + 4 + GuildMemberNameLength];
        WriteGuildHeader(packet, Opcodes.ConsortiaMemberDel);
        var body = packet.AsSpan(4);
        WriteUInt32(body, 0x00, objectId);
        PacketText.WriteFixedAscii(
            body.Slice(0x04, GuildMemberNameLength),
            memberName);
        return packet;
    }

    /// <summary>
    /// <c>MSG_CONSORTIA_INVITE</c> (<c>10146</c>): tells a guild's officers that
    /// somebody applied to join.
    /// </summary>
    /// <remarks>
    /// The client's case for this opcode (<c>0x4ed3dc</c>) reads one string, at
    /// <c>body+4</c>, and prints it with its own <c>"RequestJoinCon"</c> text (a
    /// notice plus a chat line); it reads nothing else, so the leading dword is
    /// written as zero and the width matches the request the window itself sends
    /// for the same action (104 bytes, measured 2026-10-02 at <c>0x540502</c>).
    /// </remarks>
    public static byte[] GuildJoinApplicationNotice(string applicantName)
    {
        ArgumentNullException.ThrowIfNull(applicantName);
        var packet = new byte[4 + 0x64];
        WriteGuildHeader(packet, Opcodes.ConsortiaInviteConfirm);
        var body = packet.AsSpan(4);
        WriteUInt32(body, 0x00, 0);
        PacketText.WriteFixedAscii(
            body.Slice(0x04, GuildNameLength),
            applicantName);
        return packet;
    }

    private static void WriteUInt32(Span<byte> body, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(body[offset..], value);

    private static void WriteGuildHeader(byte[] packet, ushort opcode)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
    }
}
