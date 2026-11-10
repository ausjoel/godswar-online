using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static class DuelArenaCapturedProtocolChecks
{
    public const string CheckName = "Duel Arena captured seven-NPC roster and travel";

    public static Task RunAsync()
    {
        var arguments = Enumerable.Repeat(-1, 18).ToArray();
        CheckRandomEntryPool(arguments);
        arguments[17] = int.MaxValue;
        Check.True(DuelArenaCapturedTransportProtocol.TryResolveDestination(
            "Arena_003", 5199, 57, 87, -1, arguments, out var lower) &&
            lower.TargetX == -5f && lower.TargetZ == 32f,
            "captured Gatekeeper dialog 87 enters the lower arena");
        Check.True(DuelArenaCapturedTransportProtocol.TryResolveDestination(
            "Arena_004", 5201, 57, 88, -1, arguments, out var upper) &&
            upper.TargetX == -104f && upper.TargetZ == 96f,
            "captured Ward dialog 88 returns to the upper lobby");
        Check.True(!DuelArenaCapturedTransportProtocol.TryResolveDestination(
            "Arena_002", 5197, 57, 88, -1, arguments, out _) &&
            !DuelArenaCapturedTransportProtocol.TryResolveDestination(
                "Arena_003", 5199, 57, 88, -1, arguments, out _) &&
            !DuelArenaCapturedTransportProtocol.TryResolveDestination(
                "Arena_003", 5199, 56, 87, -1, arguments, out _) &&
            !DuelArenaCapturedTransportProtocol.TryResolveDestination(
                "Arena_003", 5199, 57, 87, 1001, arguments, out _) &&
            !DuelArenaCapturedTransportProtocol.TryResolveDestination(
                "Arena_003", 5199, 57, 87, -1, arguments[..6], out _),
            "capture travel rejects wrong actor, dialog, map, action and packet geometry");

        var arena = NpcContentBaselineV7.LoadDefinitions()
            .Where(npc => npc.MapId == 57).ToArray();
        var tracker = new WorldSectorVisibilityTracker<NpcSpawnDefinition>(
            arena, npc => npc.ObjectId, npc => npc.X, npc => npc.Z, "captured Arena NPC");
        Check.True(tracker.TryCalculate(-104f, 96f, out var lobby) &&
            lobby.Entering.Select(npc => npc.ObjectId)
                .SequenceEqual(new uint[] { 5197, 5198, 5199, 5200, 5202, 5203 }),
            "captured arrival exposes the six observed lobby actors");
        tracker.Commit(lobby);
        Check.True(tracker.TryCalculate(-5f, 32f, out var arenaFloor) &&
            arenaFloor.Entering.Select(npc => npc.ObjectId)
                .SequenceEqual(new uint[] { 5201 }),
            "captured lower arrival exposes the Arena Ward");

        var stream = PacketBuilder.NpcSpawns(arena);
        Check.Equal(7 * 108, stream.Length,
            "Origin receives seven complete native appearance frames");
        for (var index = 0; index < arena.Length; index++)
        {
            var packet = stream.AsSpan(index * 108, 108);
            Check.True(BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]) == 10020 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]) == 0x00390211 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]) == arena[index].ObjectId &&
                BinaryPrimitives.ReadSingleLittleEndian(packet[28..]) == arena[index].X &&
                BinaryPrimitives.ReadSingleLittleEndian(packet[36..]) == arena[index].Z &&
                BinaryPrimitives.ReadInt32LittleEndian(packet[40..]) == 0x4014999A,
                "emitted Arena appearance preserves captured type, identity, position and facing bits");
        }
        return Task.CompletedTask;
    }

    private static void CheckRandomEntryPool(int[] arguments)
    {
        var points = DuelArenaEntryDestinations.All;
        Check.True(points.Count == 77 && points.Distinct().Count() == points.Count &&
            points[0] == (-5f, 32f), "random entry retains 77 distinct certified positions including the capture");
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            Check.True(DuelArenaCapturedTransportProtocol.TryResolveDestination(
                    "Arena_003", 5199, 57, 87, -1, arguments, out var result, index) &&
                result.Section == DuelArenaSection.LowerArena &&
                (result.TargetX, result.TargetZ) == point &&
                Math.Pow(point.X + 5, 2) + Math.Pow(point.Z - 32, 2) <= 400,
                "every server-selected entry resolves to its certified lower-floor position");
        }
        Check.True(!DuelArenaCapturedTransportProtocol.TryResolveDestination(
                "Arena_003", 5199, 57, 87, -1, arguments, out _, -1) &&
            !DuelArenaCapturedTransportProtocol.TryResolveDestination(
                "Arena_003", 5199, 57, 87, -1, arguments, out _, points.Count),
            "out-of-range random entry selections fail closed");
    }
}
