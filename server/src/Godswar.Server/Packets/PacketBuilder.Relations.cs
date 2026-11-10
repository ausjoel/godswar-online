using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

/// <summary>One name in a friend or blacklist window.</summary>
internal readonly record struct RelationEntry(
    int CharacterId,
    string Name,
    byte Camp,
    byte Profession,
    int Level,
    bool Online);

internal static partial class PacketBuilder
{
    private static void WriteRelationHeader(Span<byte> packet, ushort opcode)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet[..2],
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(2, 2), opcode);
    }

    private static byte RelationLevelByte(int level) =>
        (byte)Math.Clamp(level, 0, byte.MaxValue);

    /// <summary>
    /// 10098. The client empties that kind's window and refills it, so this is also
    /// how a deletion reaches the window: it does not remove the row by itself.
    /// </summary>
    public static byte[] RelationList(
        uint playerObjectId,
        byte kind,
        IReadOnlyList<RelationEntry> entries)
    {
        var count = Math.Min(entries.Count, RelationProtocol.MaximumPerKind);
        var packet = new byte[
            RelationProtocol.ListHeaderBytes +
            count * RelationProtocol.ListEntryBytes];
        WriteRelationHeader(packet, Opcodes.RelationList);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            playerObjectId);
        packet[8] = kind;
        packet[9] = (byte)count;
        for (var index = 0; index < count; index++)
        {
            var entry = entries[index];
            var record = packet.AsSpan(
                RelationProtocol.ListHeaderBytes +
                index * RelationProtocol.ListEntryBytes,
                RelationProtocol.ListEntryBytes);
            BinaryPrimitives.WriteInt32LittleEndian(
                record[..4],
                entry.CharacterId);
            BinaryPrimitives.WriteInt32LittleEndian(
                record.Slice(4, 4),
                entry.Online ? 1 : 0);
            PacketText.WriteFixedAscii(
                record.Slice(8, RelationProtocol.MaximumNameLength),
                entry.Name);
            record[0x1a] = entry.Online ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.Slice(0x1c, 2),
                entry.Camp);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.Slice(0x1e, 2),
                entry.Profession);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.Slice(0x20, 2),
                (ushort)Math.Clamp(entry.Level, 0, ushort.MaxValue));
        }

        return packet;
    }

    /// <summary>10099, the refusal of a request: error -1, 0 or 2 each show a line.</summary>
    public static byte[] RelationRequestError(
        string requesterName,
        string targetName,
        byte kind,
        sbyte error)
    {
        var packet = new byte[RelationProtocol.RequestBytes];
        WriteRelationHeader(packet, Opcodes.RelationRequest);
        PacketText.WriteFixedAscii(
            packet.AsSpan(4, RelationProtocol.RoleNameBytes),
            requesterName);
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                4 + RelationProtocol.RoleNameBytes,
                RelationProtocol.RoleNameBytes),
            targetName);
        packet[4 + RelationProtocol.RoleNameBytes * 2] = kind;
        packet[5 + RelationProtocol.RoleNameBytes * 2] = unchecked((byte)error);
        return packet;
    }

    /// <summary>
    /// 10100. With <paramref name="asking"/> the client opens RequestMsgBox for that
    /// name; without it the client shows its RefuseRequest line.
    /// </summary>
    public static byte[] RelationResponse(string name, bool asking)
    {
        var packet = new byte[RelationProtocol.ResponseBytes];
        WriteRelationHeader(packet, Opcodes.RelationResponse);
        PacketText.WriteFixedAscii(
            packet.AsSpan(4, RelationProtocol.RoleNameBytes),
            name);
        packet[4 + RelationProtocol.RoleNameBytes] = asking ? (byte)1 : (byte)0;
        return packet;
    }

    /// <summary>10102, one new row. Kind 0 also shows the "now your friend" line.</summary>
    public static byte[] RelationAdded(RelationEntry entry, byte kind)
    {
        var packet = new byte[RelationProtocol.AddedBytes];
        WriteRelationHeader(packet, Opcodes.RelationAdded);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4, 4),
            entry.CharacterId);
        PacketText.WriteFixedAscii(
            packet.AsSpan(8, RelationProtocol.MaximumNameLength),
            entry.Name);
        packet[0x28] = entry.Camp;
        packet[0x29] = entry.Online ? (byte)1 : (byte)0;
        packet[0x2a] = entry.Profession;
        packet[0x2b] = RelationLevelByte(entry.Level);
        packet[0x2c] = entry.Online ? (byte)1 : (byte)0;
        packet[0x2d] = kind;
        return packet;
    }

    /// <summary>
    /// 10103. The client finds the row by the id in the list entry; a positive second
    /// field marks it online. The last byte asks it to redraw an open window.
    /// </summary>
    public static byte[] RelationPresence(int characterId, bool online, int level)
    {
        var packet = new byte[RelationProtocol.PresenceBytes];
        WriteRelationHeader(packet, Opcodes.RelationPresence);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4, 4),
            characterId);
        BinaryPrimitives.WriteInt16LittleEndian(
            packet.AsSpan(8, 2),
            online ? (short)1 : (short)0);
        packet[10] = RelationLevelByte(level);
        packet[11] = 1;
        return packet;
    }
}
