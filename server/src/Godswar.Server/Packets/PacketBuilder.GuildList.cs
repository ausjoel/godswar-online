using System.Buffers.Binary;
using Godswar.Server.Application.Guilds;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int GuildListElementLength = 0x7C;
    private const int GuildListNameLength = 0x40;
    private const int GuildListLordNameLength = 0x20;
    private const int GuildListMaximumElements = 15;
    private const int GuildListTailFlagOffset = 0x748;
    private const int GuildListKeyOffset = 0x60;
    private const int GuildListLevelOffset = 0x64;
    private const int GuildListFlagOffset = 0x65;
    private const int GuildListMemberCountOffset = 0x6C;
    private const int GuildListMemberLimitOffset = 0x70;
    private const int GuildListFootstoneLevelOffset = 0x74;
    private const int GuildListBuildingCountOffset = 0x78;

    /// <summary>
    /// <c>MSG_CONSORTIA_ELEMENT_LIST</c> (<c>10157</c>): the guilds the guild
    /// window's List tab shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recovered from the client's own case (<c>0x4edfc3</c>): the count is read
    /// at <c>body+0</c> and the entries follow from <c>body+4</c>, 0x7C bytes
    /// apart. Five ELEMENT_LIST opcodes exist and this is the only one whose body
    /// is a whole array; the other four (<c>10158</c>-<c>10161</c>) operate on a
    /// single entry. The message is a fixed fifteen-slot array: with fifteen
    /// entries the array ends exactly where the handler's own tail flag sits
    /// (<c>0x4edfe4 cmp byte ptr [esi+0x74c]</c> is <c>body+0x748</c>, and
    /// <c>4 + 15*0x7C = 0x748</c>), and that flag means "this is the whole list" -
    /// the client clears its list container before adding the rows when it is set.
    /// </para>
    /// <para>
    /// Entry layout, from the same case and from the row builder
    /// (<c>0x5460e0</c>): the guild name at <c>+0x00</c> (64 bytes), the lord's
    /// name at <c>+0x40</c> (32 bytes), the entry key at <c>+0x60</c> (the insert
    /// uses <c>element+0x60</c> as the key), then the tab's three columns - the
    /// level byte at <c>+0x64</c>, the member pair at <c>+0x6C</c>/<c>+0x70</c>
    /// and the building pair at <c>+0x74</c>/<c>+0x78</c>. <c>+0x65</c> is a byte
    /// the row tests and <c>+0x68</c> is not read by the row builder at all.
    /// </para>
    /// <para>
    /// <b>Measured 2026-10-02</b> (first in-client readings of the tab): the level
    /// column printed <c>+0x64</c>, the member column the <c>+0x6C</c>/<c>+0x70</c>
    /// pair and the building column the <c>+0x74</c>/<c>+0x78</c> pair - with the
    /// earlier field order that showed the member count under Level, the member
    /// limit and online count under Member, and the silver and gold amounts under
    /// Building. The building pair is the guild's <b>footstone level and its
    /// building count</b>, in that order, as the player confirmed against the
    /// window (building type 1 is the Guild Footstone).
    /// </para>
    /// </remarks>
    public static byte[] GuildElementList(
        IReadOnlyList<GuildListEntry> guilds)
    {
        ArgumentNullException.ThrowIfNull(guilds);
        var packet = new byte[4 + GuildListTailFlagOffset + 1];
        WriteGuildHeader(packet, Opcodes.ConsortiaElementList);
        var body = packet.AsSpan(4);
        var count = Math.Min(guilds.Count, GuildListMaximumElements);
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)count);
        for (var index = 0; index < count; index++)
        {
            var guild = guilds[index];
            var entry = body.Slice(
                4 + (index * GuildListElementLength),
                GuildListElementLength);
            PacketText.WriteFixedAscii(
                entry[..GuildListNameLength],
                guild.Name);
            PacketText.WriteFixedAscii(
                entry.Slice(
                    GuildListNameLength,
                    GuildListLordNameLength),
                guild.LordName);
            WriteUInt32(
                entry,
                GuildListKeyOffset,
                unchecked((uint)guild.GuildId));
            // The client's own row builder (0x5460e0) reads these slots and the
            // window's column buttons name them (Consortia.xml): ConLev (Level),
            // ConMember (成员/上限) and ConBuild (公会建筑). Measured in the client
            // 2026-10-02 with the previous field order: +0x64 printed under Level,
            // the +0x6C/+0x70 pair under Member, and the +0x74/+0x78 pair under
            // Building, so the columns are fixed even where the values were wrong.
            entry[GuildListLevelOffset] = guild.Level;
            // +0x65 is the guild's "refuse applications" switch. Measured
            // 2026-10-02: the row builder branches on it (`0x5466db cmp byte ptr
            // [ebp+0x65], 0`) and draws the Join cell with the client's own
            // "ConReject" text when it is set, which is the greyed-out state.
            entry[GuildListFlagOffset] = guild.RefusesApplications
                ? (byte)1
                : (byte)0;
            WriteUInt32(
                entry,
                GuildListMemberCountOffset,
                unchecked((uint)Math.Max(0, guild.MemberCount)));
            WriteUInt32(
                entry,
                GuildListMemberLimitOffset,
                unchecked((uint)Math.Max(0, guild.MemberLimit)));
            WriteUInt32(
                entry,
                GuildListFootstoneLevelOffset,
                unchecked((uint)Math.Max(0, guild.FootstoneLevel)));
            WriteUInt32(
                entry,
                GuildListBuildingCountOffset,
                unchecked((uint)Math.Max(0, guild.BuildingCount)));
        }

        body[GuildListTailFlagOffset] = 1;
        return packet;
    }
}
