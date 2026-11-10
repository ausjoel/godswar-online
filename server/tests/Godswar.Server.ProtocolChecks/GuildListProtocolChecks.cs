using System.Buffers.Binary;
using Godswar.Server.Application.Guilds;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The guild window's List tab: <c>S2C 10157 MSG_CONSORTIA_ELEMENT_LIST</c> is a
/// fixed fifteen-slot array - count at <c>body+0</c>, entries of 0x7C bytes from
/// <c>body+4</c>, and the whole-list flag at <c>body+0x748</c>, which is exactly
/// where fifteen entries end (<c>4 + 15*0x7C</c>).
/// </summary>
internal static class GuildListProtocolChecks
{
    public const string CheckName =
        "Guild list element array (10157 shape and slots)";

    private const int ElementLength = 0x7C;
    // body+0 is the count and the entries follow from body+4, so the first entry
    // starts at packet offset 4 + 4.
    private const int FirstElementOffset = 8;
    private const int TailFlagOffset = 4 + 0x748;

    public static Task RunAsync()
    {
        var guilds = new[]
        {
            new GuildListEntry(
                GuildId: 6,
                Name: "567",
                LordName: "test",
                Level: 4,
                MemberCount: 2,
                MemberLimit: 100,
                OnlineCount: 1,
                Silver: 3_045_125,
                Gold: 2_290_123,
                Bijou: 7,
                BuildingCount: 17,
                FootstoneLevel: 2,
                RefusesApplications: false),
            new GuildListEntry(
                GuildId: 7,
                Name: "666",
                LordName: "test2",
                Level: 1,
                MemberCount: 1,
                MemberLimit: 150,
                OnlineCount: 1,
                Silver: 11,
                Gold: 12,
                Bijou: 0,
                BuildingCount: 1,
                FootstoneLevel: 0,
                RefusesApplications: true)
        };

        var packet = PacketBuilder.GuildElementList(guilds);
        Check.Equal(1869, packet.Length, "element list packet length");
        Check.Equal(
            (ushort)packet.Length,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "element list declared length");
        Check.Equal(
            (ushort)10157,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "element list opcode");
        Check.Equal(
            2u,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
            "element list count at body+0");
        Check.Equal(1, packet[TailFlagOffset], "whole-list flag at body+0x748");

        CheckEntry(packet, 0, "567", "test", 6, 4, 2, 100, 2, 17, refuses: false);
        CheckEntry(packet, 1, "666", "test2", 7, 1, 1, 150, 0, 1, refuses: true);

        var empty = PacketBuilder.GuildElementList([]);
        Check.Equal(1869, empty.Length, "empty element list keeps its shape");
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(empty.AsSpan(4)),
            "empty element list count");
        Check.Equal(1, empty[TailFlagOffset], "empty element list still refreshes");

        return Task.CompletedTask;
    }

    private static void CheckEntry(
        byte[] packet,
        int index,
        string name,
        string lord,
        long guildId,
        int level,
        int memberCount,
        int memberLimit,
        int footstoneLevel,
        int buildingCount,
        bool refuses)
    {
        var entry = packet.AsSpan(
            FirstElementOffset + (index * ElementLength),
            ElementLength);
        Check.Equal(
            name,
            ReadFixedAscii(entry[..0x40]),
            $"entry {index} guild name");
        Check.Equal(
            lord,
            ReadFixedAscii(entry.Slice(0x40, 0x20)),
            $"entry {index} lord name");
        Check.Equal(
            (uint)guildId,
            BinaryPrimitives.ReadUInt32LittleEndian(entry[0x60..]),
            $"entry {index} key at +0x60");
        Check.Equal(
            (byte)level,
            entry[0x64],
            $"entry {index} level at +0x64 (Level column)");
        Check.Equal(
            refuses ? (byte)1 : (byte)0,
            entry[0x65],
            $"entry {index} refuse-applications flag at +0x65");
        Check.Equal(
            (uint)memberCount,
            BinaryPrimitives.ReadUInt32LittleEndian(entry[0x6C..]),
            $"entry {index} member count at +0x6C (Member column, left)");
        Check.Equal(
            (uint)memberLimit,
            BinaryPrimitives.ReadUInt32LittleEndian(entry[0x70..]),
            $"entry {index} member limit at +0x70 (Member column, right)");
        Check.Equal(
            (uint)footstoneLevel,
            BinaryPrimitives.ReadUInt32LittleEndian(entry[0x74..]),
            $"entry {index} footstone level at +0x74 (Building column, left)");
        Check.Equal(
            (uint)buildingCount,
            BinaryPrimitives.ReadUInt32LittleEndian(entry[0x78..]),
            $"entry {index} building count at +0x78 (Building column, right)");
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(
            end < 0 ? field : field[..end]);
    }
}
