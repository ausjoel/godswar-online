using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int QuestAppraisalPacketLength = 8;

    /// <summary>
    /// The quest window's appraisal answer.
    /// </summary>
    /// <remarks>
    /// The stock client sends C2S 10093 as a six-byte frame - length, opcode and
    /// two payload bytes that were zero in all four captured clicks - when the
    /// player presses 我要鉴定 / "Appraisal" on the 经验加成 tab. The reference
    /// server answers an eight-byte frame of the same opcode whose only field is
    /// one 32-bit value, also zero in every capture. This server fills that field
    /// with the character's permanent bonus in basis points, so "no bonus" still
    /// reproduces the captured frame byte for byte. The reference gate and the
    /// meaning of a nonzero answer are not attested by any capture.
    /// </remarks>
    public static byte[] QuestAppraisal(int bonusBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bonusBasisPoints);
        var packet = new byte[QuestAppraisalPacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)QuestAppraisalPacketLength));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.QuestAppraisal);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            bonusBasisPoints);
        return packet;
    }
}
