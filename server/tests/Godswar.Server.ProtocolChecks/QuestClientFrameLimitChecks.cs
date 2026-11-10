using System.Buffers.Binary;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// QUEST-SOCIAL (2026-11-02): frames the Origin client can actually take.
/// </summary>
/// <remarks>
/// The client asserts nsMsgSize &lt;= 2048 on every received frame and then faults
/// (seen on live: a login carrying seven quests, 12:57 UTC), so the login quest list
/// goes out three quests per frame. 10083 is read as MSG_NPC_CANCELQUEST
/// {NPCID, RewardNPCID, QuestID, Available}. 10077 / 10080 fill twenty slots on the
/// client npc.
/// </remarks>
internal static class QuestClientFrameLimitChecks
{
    public const string CheckName = "Quest frames fit the client";

    public static Task RunAsync()
    {
        Check.Equal(3, PacketBuilder.QuestSnapshotEntriesPerFrame, "three quests per 10090 frame");
        foreach (var count in (int[])[0, 1, 3, 4, 7, 20])
        {
            var entries = new PacketBuilder.QuestSnapshotEntry[count];
            for (var index = 0; index < count; index++)
            {
                entries[index] = new PacketBuilder.QuestSnapshotEntry(518u + (uint)index, 5091u, 5103u);
            }

            var frames = PacketBuilder.QuestSnapshotFrames(entries);
            Check.Equal(Math.Max(1, (count + 2) / 3), frames.Count, $"{count} quests: frame count");
            var seen = new List<uint>();
            foreach (var frame in frames)
            {
                Check.True(frame.Length <= 2048, $"{count} quests: frame of {frame.Length} bytes fits 2048");
                Check.Equal(frame.Length, (int)BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2)), $"{count} quests: declared length");
                Check.Equal((ushort)10090, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2)), $"{count} quests: opcode");
                var inFrame = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4));
                Check.True(inFrame <= 3, $"{count} quests: at most three per frame");
                for (var index = 0; index < inFrame; index++)
                {
                    seen.Add(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8 + (index * 680), 4)));
                }
            }

            Check.Equal(count, seen.Count, $"{count} quests: every quest published once");
            for (var index = 0; index < seen.Count; index++)
            {
                Check.Equal(518u + (uint)index, seen[index], $"{count} quests: order kept at {index}");
            }
        }

        var cancel = PacketBuilder.QuestCancelAnswer(5245u, 5477u, 1184u);
        Check.Equal(17, cancel.Length, "cancel answer length");
        Check.Equal((ushort)10083, BinaryPrimitives.ReadUInt16LittleEndian(cancel.AsSpan(2, 2)), "cancel answer opcode");
        Check.Equal(5245u, BinaryPrimitives.ReadUInt32LittleEndian(cancel.AsSpan(4, 4)), "cancel answer NPCID is the giver");
        Check.Equal(5477u, BinaryPrimitives.ReadUInt32LittleEndian(cancel.AsSpan(8, 4)), "cancel answer RewardNPCID is the responder");
        Check.Equal(1184u, BinaryPrimitives.ReadUInt32LittleEndian(cancel.AsSpan(12, 4)), "cancel answer QuestID");

        var many = new List<(uint QuestId, uint Available)>();
        for (var index = 0; index < 30; index++)
        {
            many.Add((2000u + (uint)index, index >= 25 ? 1u : 0u));
        }

        var markers = PacketBuilder.QuestMarkerList(5000u, many);
        Check.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(markers.AsSpan(8, 4)), "10077 keeps twenty entries");
        Check.Equal(12 + (20 * 8), markers.Length, "10077 length for twenty");
        var availableKept = 0;
        for (var index = 0; index < 20; index++)
        {
            availableKept += (int)BinaryPrimitives.ReadUInt32LittleEndian(markers.AsSpan(12 + (index * 8) + 4, 4));
        }

        Check.Equal(5, availableKept, "10077 keeps the available entries first");
        var handIn = PacketBuilder.QuestHandInMenu(5000u, [.. Enumerable.Range(0, 30).Select(index => 1000u + (uint)index)]);
        Check.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(handIn.AsSpan(8, 4)), "10080 keeps twenty entries");
        return Task.CompletedTask;
    }
}
