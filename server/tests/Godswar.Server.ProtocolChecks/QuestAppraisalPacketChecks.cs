using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class QuestAppraisalPacketChecks
{
    public const string CheckName = "Quest experience appraisal packet";

    public static Task RunAsync()
    {
        // Byte-for-byte golden frame from the reference capture: all four clicked
        // 10093 requests were identical and every answer carried a zero state.
        // Reproduced by docs/quest-experience-appraisal-20260924.md.
        var withheld = PacketBuilder.QuestAppraisal(0);
        Check.Equal(
            "08006D2700000000",
            Convert.ToHexString(withheld),
            "captured zero-state appraisal answer");

        Check.Equal(
            10093,
            (int)Opcodes.QuestAppraisal,
            "quest window appraisal opcode");

        var granted = PacketBuilder.QuestAppraisal(
            GameClientHandler.QuestAppraisalPolicy.BonusBasisPoints);
        Check.Equal(8, granted.Length, "granted answer length");
        Check.Equal(
            (ushort)8,
            BinaryPrimitives.ReadUInt16LittleEndian(granted),
            "granted answer declared length");
        Check.Equal(
            Opcodes.QuestAppraisal,
            BinaryPrimitives.ReadUInt16LittleEndian(granted.AsSpan(2)),
            "granted answer opcode");
        Check.Equal(
            1_000,
            GameClientHandler.QuestAppraisalPolicy.BonusBasisPoints,
            "appraisal pays ten percent");
        Check.Equal(
            1_000,
            BinaryPrimitives.ReadInt32LittleEndian(granted.AsSpan(4)),
            "granted answer state value");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.QuestAppraisal(-1),
            "negative appraisal bonus is rejected");
        return Task.CompletedTask;
    }
}
