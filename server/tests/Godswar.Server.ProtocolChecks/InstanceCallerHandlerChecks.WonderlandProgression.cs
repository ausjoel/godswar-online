using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckWonderlandProgressionDoesNotClearLootAsync(InstanceCallerFixture leader,
        MonsterRuntimeSnapshot boss)
    {
        var expected = new[]
        {
            Convert.FromHexString("100074280F0000004814000040E20100"),
            Convert.FromHexString("10007428330000004814000015030000"),
            Convert.FromHexString("10007428310000004814000025000000")
        };
        Check.True(PacketBuilder.WonderlandMonsterProgression(0x1448, 123456, 789, 37)
            .Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second)),
            "Wonderland progression uses captured10356 actor attributes15/51/49 for EXP/talent EXP/talent points");
        var before = leader.ReadPackets().Count;
        var publish = typeof(GameClientHandler).GetMethod("SendMonsterDeathProgressionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Reproduce the former race: a durable settlement resumes after10029 has
        // already made this boss corpse sparkle. Replay must also be harmless.
        for (var replay = 0; replay < 2; replay++)
            await (Task)publish.Invoke(leader.Handler,
                [boss.ObjectId, boss.SpawnGeneration, 123456L, 789, 37, CancellationToken.None])!;
        var packets = leader.ReadPackets().Skip(before).ToArray();
        Check.True(packets.Count(packet => ReadOpcode(packet) == 10356) == 6 &&
            packets.All(packet => ReadOpcode(packet) != 10027) &&
            expected.All(golden => packets.Count(packet => packet.SequenceEqual(golden)) == 2),
            "late or replayed kill progression updates every reward total without wiping the existing corpse loot");
        Check.True(leader.Registry.TryResolveWonderlandBossLootPickup(leader.Session, boss.ObjectId, 0,
                DateTimeOffset.UtcNow, out var request) && request.SackItemId == 4450,
            "a delayed progression refresh leaves the original Alpha sack claimable");
    }
}
