using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckAtlantisStationaryWaveVisibilityAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var leader = fixture.Leader;
        await EnterAtlantisAsync(fixture, InstanceCallerProtocol.AtlantisEnterSubId);
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        var instanceId = GetSourceInstanceId(leader);
        Check.True(leader.Registry.TryGetAtlantisEncounterSnapshot(instanceId, out var run),
            "stationary visibility fixture owns an Atlantis clock");

        // Simulate arrival in the verified central room, then keep the player
        // stationary: the next group must publish without a movement request.
        leader.Character.PositionX = -74f;
        leader.Character.PositionZ = 31f;
        var beforeFirst = leader.ReadPackets().Count;
        await leader.Registry.AdvanceMonsterWorldOnceAsync(run.StartedAt, CancellationToken.None);
        var first = leader.Registry.GetMapMonsterSnapshots(leader.Session, 205)
            .Where(static monster => monster.IsAlive && IsAtlantisScoringObject(monster.ObjectId)).ToArray();
        Check.Equal(12, first.Length, "the first group has exactly 12 living monsters");
        AssertAtlantisAppearances(leader.ReadPackets().Skip(beforeFirst),
            first.Select(static monster => monster.ObjectId).ToHashSet());

        foreach (var monster in first)
        {
            Check.True(leader.Registry.TryApplyMonsterDamage(205, monster.ObjectId,
                monster.CurrentHealth, leader.Character.Id, out var death) && death.Killed,
                "a stationary observer's first group dies through the exact registry route");
            var committed = leader.Registry.CaptureAtlantisMonsterKill(leader.Session, death);
            Check.True(committed is not null, "the death settlement captures an Atlantis projection");
            await MonsterDeathRewardCommitBoundary.ExecuteAsync(
                _ => Task.FromResult(true), allowImmediateReplay: true,
                onSettled: _ => committed!());
        }
        var beforeSecond = leader.ReadPackets().Count;
        await leader.Registry.AdvanceMonsterWorldOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        var second = leader.Registry.GetMapMonsterSnapshots(leader.Session, 205)
            .Where(static monster => monster.IsAlive && IsAtlantisScoringObject(monster.ObjectId)).ToArray();
        Check.True(second.Length == 12 &&
            !second.Select(static monster => monster.ObjectId)
                .Intersect(first.Select(static monster => monster.ObjectId)).Any(),
            "next group has distinct death identities without respawning the first group");
        AssertAtlantisAppearances(leader.ReadPackets().Skip(beforeSecond),
            second.Select(static monster => monster.ObjectId).ToHashSet());
        Check.True(leader.Registry.TryGetAtlantisEncounterSnapshot(instanceId, out var progress) &&
            progress.TeamPoints == 30, "the actual registry settlement callback clears one 30-point group");
    }

    private static void AssertAtlantisAppearances(
        IEnumerable<byte[]> packets, IReadOnlySet<uint> expected)
    {
        var appearances = packets.Where(static packet => packet.Length >= 108 && ReadOpcode(packet) == 10020 &&
                (BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) & 0xFF) == 0x12)
            .ToArray();
        Check.True(appearances.All(static packet =>
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)) == 205),
            "emitted Atlantis appearances survive the stock client's map-205 rejection gate");
        var actual = appearances.Where(static packet =>
                IsAtlantisScoringObject(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8))))
            .Select(static packet =>
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8))).ToArray();
        Check.True(actual.Length == expected.Count && expected.SetEquals(actual),
            "world tick publishes every current group appearance exactly once to a stationary observer");
    }
}
