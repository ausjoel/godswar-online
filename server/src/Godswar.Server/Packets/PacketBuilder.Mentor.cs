using System.Buffers.Binary;
using System.Text;

namespace Godswar.Server.Packets;

// ECONOMY mentor (Master and Apprentice, 2026-11-03). Frame layouts read from the stock 2.51 client (Q20 GodsWar.exe,
// class CPrentice, see Development/build/economy/mentor/README.md for the addresses):
//  - MSG_PRENTICE 10234, 72 bytes both ways (PROVEN: every client send site builds 0x48 bytes, and the receive handler
//    0x5ce7b0 copies 0x48 bytes): +4 u32 operation, +8 char[32] master name, +40 char[32] apprentice name.
//    Client -> server operations: 0 invite (master side), 2 leave the master (apprentice), 3 expel an apprentice
//    (master, apprentice name at +40), 5 ask for the list (sent each time the Friends window opens), 6 accept the
//    invitation, 7 refuse it (6/7 echo the invitation frame). Server -> client: operation 0 only (the invitation:
//    the client shows "<master>Do you need a master?" and answers 6 or 7).
//  - MSG_PRENTICELIST 10235 (UNPROVEN size; the reader 0x5cf040 touches bytes up to +0x163): +4 u32 count (<= 6),
//    +8 six 44-byte rows {+0 name, +0x24 u16 map, +0x26 u16 level, +0x28 u8 online}, +0x110 the master's row,
//    +0x13c char[32] master name (MasterNameText), +0x15c u32 Masters Ethics (MaterValueText), +0x160 u32 (hidden).
//  - MSG_PRENITCE_TIP 10236 (UNPROVEN size; the reader 0x5cf930 reads +4 code, +8 value, +12 name): codes 8..29 pick
//    a Message.dat line (9 YourPrentice+name+Prentice_Graduated, 10 Prentice_Graduated, 11 GetExp, 12 GetSkillPoint,
//    13 GetMoney, 15 GetUntradeStone (B-Gold), 19 AbandonMaster, 20 AbandonPrentice, 21 BeAbandonedByMaster,
//    22 YourPrentice+name+BeAbandonedByPrentice, 25 Prentice_Cannot, 26 Prenitce_Full, 27 AlreadyHadMaster,
//    28 Disagreed, 29 Prentice_SUCCESSFUL).
internal static partial class PacketBuilder
{
    internal const ushort MentorOpcode = 10234;
    internal const ushort MentorListOpcode = 10235;
    internal const ushort MentorTipOpcode = 10236;
    internal const int MentorFrameBytes = 72;
    internal const int MentorListBytes = 0x164;
    internal const int MentorTipBytes = 48;
    internal const int MentorListRows = 6;

    internal readonly record struct MentorListRow(string Name, ushort MapId, ushort Level, bool Online);

    public static byte[] MentorInvitation(string masterName, string apprenticeName)
    {
        var packet = new byte[MentorFrameBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, MentorFrameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), MentorOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0);
        WriteMentorName(packet.AsSpan(8, 32), masterName);
        WriteMentorName(packet.AsSpan(40, 32), apprenticeName);
        return packet;
    }

    public static byte[] MentorList(MentorListRow? master, IReadOnlyList<MentorListRow> apprentices, int mastersEthics = 0)
    {
        var packet = new byte[MentorListBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, MentorListBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), MentorListOpcode);
        var count = Math.Min(MentorListRows, apprentices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), checked((uint)count));
        for (var index = 0; index < count; index++)
            WriteMentorRow(packet.AsSpan(8 + (index * 44), 44), apprentices[index]);
        if (master is { } row)
        {
            WriteMentorRow(packet.AsSpan(0x110, 44), row);
            WriteMentorName(packet.AsSpan(0x13c, 32), row.Name);
        }

        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0x15c), Math.Max(0, mastersEthics));
        return packet;
    }

    public static byte[] MentorTip(int code, int value = 0, string? name = null)
    {
        var packet = new byte[MentorTipBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, MentorTipBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), MentorTipOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), code);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), value);
        WriteMentorName(packet.AsSpan(12, 32), name ?? string.Empty);
        return packet;
    }

    /// <summary>The NUL-terminated name in a client name field.</summary>
    internal static string ReadMentorName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]).Trim();
    }

    private static void WriteMentorRow(Span<byte> target, MentorListRow row)
    {
        WriteMentorName(target[..32], row.Name);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x24..], row.MapId);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x26..], row.Level);
        target[0x28] = row.Online ? (byte)1 : (byte)0;
    }

    private static void WriteMentorName(Span<byte> field, string name)
    {
        field.Clear();
        var bytes = Encoding.ASCII.GetBytes(name);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length - 1)).CopyTo(field);
    }
}
