using System.Buffers.Binary;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static async Task CheckLifeAbsorptionBurstAsync(PlayerRuntimeMode mode)
    {
        const int hitCount = 140; // Exceeds the ordinary outbound queue's 128-item capacity.
        await using var fixture = await Fixture.CreateAsync($"LifeBurst{mode}",
            currentHp: 100, playerRuntimeMode: mode, lifeAbsorptionFlat: 1);
        var committer = new PveLifeAbsorptionCommitter();
        var hits = Enumerable.Range(0, hitCount).Select(index => new PveCommittedMonsterDamage(
            checked(600_000UL + (ulong)index), checked(20_000u + (uint)index), 1, 10)).ToArray();
        // The real four-target handler cases establish damage authority. This
        // bounded publication case supplies committed-hit receipts directly to
        // exercise a large native frame burst without building a 140-mob world.
        var commit = fixture.Registry.CommitPveLifeAbsorption(fixture.Socket.Session,
            fixture.Character, committer, hits, 10_000);
        Check.True(commit.AppliedHealing == hitCount && commit.ClaimedHitCount == hitCount &&
            commit.HitHealing.Length == hitCount && fixture.Character.CurrentHp == 240,
            $"{mode}: the shared committer retains all 140 one-HP monster allocations");
        for (var index = 0; index < hits.Length; index++)
        {
            var allocation = commit.HitHealing[index];
            Check.True(allocation.CombatEventId == hits[index].CombatEventId &&
                allocation.MonsterObjectId == hits[index].MonsterObjectId &&
                allocation.MonsterSpawnGeneration == hits[index].MonsterSpawnGeneration &&
                allocation.AppliedHealing == 1,
                "each burst allocation retains the exact committed monster/event/generation identity");
        }
        fixture.Registry.PublishPveLifeAbsorption(commit, CancellationToken.None);
        var nativeHp = 100;
        for (var index = 0; index < hitCount; index++)
        {
            var packet = await fixture.Socket.ReadPacketAsync(32);
            AssertLifeAbsorptionHealingPacket(packet, 1, 0, 0, $"{mode} burst tick {index}");
            nativeHp -= BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(16));
        }
        var vitals = await fixture.Socket.ReadPacketAsync(16);
        Check.True(ReadOpcode(vitals) == 10097 &&
            BinaryPrimitives.ReadUInt32LittleEndian(vitals.AsSpan(4)) == LocalObjectId &&
            BinaryPrimitives.ReadInt32LittleEndian(vitals.AsSpan(8)) == nativeHp &&
            BinaryPrimitives.ReadInt32LittleEndian(vitals.AsSpan(12)) == InitialMana &&
            nativeHp == fixture.Character.CurrentHp && !fixture.Socket.Session.IsDisconnected,
            "140 separate native healing frames and final vitals survive one atomic stream admission");
        var replay = fixture.Registry.CommitPveLifeAbsorption(fixture.Socket.Session,
            fixture.Character, committer, hits, 10_000);
        Check.True(!replay.Applied && replay.ClaimedHitCount == 0 && replay.HitHealing.IsDefaultOrEmpty,
            "the complete 140-target replay has no new healing allocation");
        fixture.Registry.PublishPveLifeAbsorption(replay, CancellationToken.None);
        await Task.Delay(50);
        Check.True(fixture.Socket.Available == 0 && !fixture.Socket.Session.IsDisconnected,
            "burst replay adds no duplicate frame and never disconnects a healthy recipient");
    }
}
