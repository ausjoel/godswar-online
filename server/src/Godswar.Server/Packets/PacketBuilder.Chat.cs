using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    /// <summary>
    /// The stock chat frame (10035) as the client itself writes it: the speaker's
    /// object id, the text's byte count including its terminator, the channel word,
    /// then the text in UTF-16LE.
    /// </summary>
    /// <remarks>
    /// This is also the frame that carries a message to another map, because the
    /// text is UTF-16 and so survives non-ASCII names and messages. The composer
    /// takes the text exactly as the client wrote it - the client already prefixes
    /// the speaker's name - so a relayed message never grows a second name.
    /// </remarks>
    /// <summary>
    /// Unknown to the stock client. Q20-Client/patch_gm_walls.py reads payload byte 0 (walk through walls);
    /// finalise gmclient/patch_gm_speed.py also reads byte 1 (let the 10166 speed multiplier act on foot).
    /// </summary>
    public static byte[] GmWallWalk(bool on, bool speedOnFoot = false)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 10999);
        packet[4] = on ? (byte)1 : (byte)0;
        packet[5] = speedOnFoot ? (byte)1 : (byte)0;
        return packet;
    }

    public static byte[] ChatMessage(
        uint speakerObjectId,
        uint channelWord,
        string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        const int headerBytes = 4;
        var encoded = Encoding.Unicode.GetBytes(text);
        var packetLength = checked(
            headerBytes +
            ChatChannelProtocol.PayloadHeaderBytes +
            encoded.Length +
            sizeof(ushort));
        if (packetLength > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                "A chat frame cannot carry more than 65535 bytes.");
        }

        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Talk);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(headerBytes),
            speakerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(headerBytes + sizeof(uint)),
            // The client includes the two-byte terminator in this count, and the
            // terminator itself follows the text.
            checked((uint)encoded.Length + sizeof(ushort)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(headerBytes + (sizeof(uint) * 2)),
            channelWord);
        encoded.CopyTo(packet.AsSpan(
            headerBytes + ChatChannelProtocol.PayloadHeaderBytes));
        return packet;
    }
}
