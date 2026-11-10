using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    internal const int PlayerAcceptedQuestsBytes = 2048;

    /// <summary>Objective row stride inside a 10090 snapshot.</summary>
    internal const int QuestObjectiveStride = 24;

    /// <summary>
    /// One objective row of an opcode-10090 quest snapshot.
    /// </summary>
    /// <remarks>
    /// Captured rows are twenty bytes on a twenty-four byte stride:
    /// <c>+0</c> objective id, <c>+4</c> completion marker, <c>+8</c> current,
    /// <c>+12</c> required, <c>+16</c> the constant 0x83, then four zero bytes.
    /// </remarks>
    internal readonly record struct QuestObjectiveRecord(
        uint ObjectiveId,
        uint Completed,
        uint Current,
        uint Required);

    /// <summary>
    /// S2C 10090: the character's accepted-quest snapshot.
    /// </summary>
    /// <remarks>
    /// Captured header: <c>+4</c> quest count, <c>+8</c> sequence, <c>+12</c> the
    /// quest giver, <c>+16</c> the quest id, <c>+20</c> objective count, then the
    /// objective rows at <c>+24</c>. The reference capture's login frame carried
    /// a quest header with no rows, and the stock client faulted while building
    /// the hand-in list from it, so the rows are published here.
    /// </remarks>
    public static byte[] PlayerAcceptedQuestsFrame(
        uint sequence,
        uint npcId,
        uint questId,
        IReadOnlyList<QuestObjectiveRecord> objectives)
    {
        ArgumentNullException.ThrowIfNull(objectives);
        var packet = new byte[PlayerAcceptedQuestsBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, sizeof(ushort)),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)),
            Opcodes.PlayerAcceptedQuests);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, sizeof(uint)),
            sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, sizeof(uint)),
            npcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16, sizeof(uint)),
            questId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, sizeof(uint)),
            (uint)objectives.Count);
        for (var index = 0; index < objectives.Count; index++)
        {
            var row = 24 + (index * QuestObjectiveStride);
            if (row + QuestObjectiveStride > packet.Length)
            {
                throw new ArgumentException(
                    "Quest objective rows exceed the snapshot frame.",
                    nameof(objectives));
            }

            var objective = objectives[index];
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(row, sizeof(uint)),
                objective.ObjectiveId);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(row + 4, sizeof(uint)),
                objective.Completed);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(row + 8, sizeof(uint)),
                objective.Current);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(row + 12, sizeof(uint)),
                objective.Required);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(row + 16, sizeof(uint)),
                0x83);
        }

        return packet;
    }
}
