using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckBossCorpseLootAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(2, monsters, players);
        var boss = f.Monster("derskey");
        f.MoveTo(boss);
        Check.True(!f.Registry.TryResolveWonderlandBossLootPickup(f.Session, boss.ObjectId, 0, f.Now, out _),
            "a living Wonderland boss never offers a sack");
        f.DirectHit("derskey", boss.CurrentHealth);
        f.Runtime.Map.TryGetWonderlandSnapshot(out var run);
        Check.True(run.CurrentIsland == 2 && run.CompletedIslands == 1 &&
            f.Registry.TryResolveWonderlandBossLootPickup(f.Session, boss.ObjectId, 0, f.Now, out var request) &&
            request.IsValid && request.SackItemId == 4451 && request.Island == 2,
            "each island-two boss offers its sack immediately on death before the other boss is killed");
        Check.True(!f.Registry.TryResolveWonderlandBossLootPickup(f.Session, boss.ObjectId, 1, f.Now, out _),
            "a corpse has exactly one native pickup slot");
        f.Runtime.Map.AdvanceMonsters(f.Now.AddSeconds(15));
        Check.True(f.Runtime.Map.TryGetMonsterSnapshot(boss.ObjectId, out var corpse) &&
            !corpse.IsAlive && corpse.IsSpawned && corpse.DespawnAt == f.Now + WonderlandBossLootPolicy.CorpseLifetime,
            "a Wonderland boss corpse has the captured twenty-second lifetime in both monster engines");
        Check.True(f.Registry.TryResolveWonderlandBossLootPickup(f.Session, boss.ObjectId, 0,
                corpse.DespawnAt!.Value.AddTicks(-1), out _) &&
            !f.Registry.TryResolveWonderlandBossLootPickup(f.Session, boss.ObjectId, 0,
                corpse.DespawnAt.Value, out _),
            "an unclaimed sack remains available up to, but never at or after, its corpse expiry");

        await using (var transition = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
                         f.Character.PositionX, f.Character.PositionZ, CancellationToken.None))
        {
            Check.True(transition is not null, "corpse viewer has a real appearance transition");
            await f.Session.SendAsync(PacketBuilder.CapturedMonsterSpawns(
                    transition!.Delta.Entering.Select(monster => monster.Appearance).ToArray()),
                CancellationToken.None, "WonderlandCorpseTestAppearance", framed: false);
            await f.Registry.SendWonderlandBossCorpseAppearancesAsync(f.Session,
                transition.Delta.Entering, CancellationToken.None);
            transition.Commit();
        }
        await f.FlushAsync();
        var packets = f.Transport.ReadLegacyPackets();
        var drops = packets.Last(packet => packet.Length == 84 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == Opcodes.MonsterDrops &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == boss.ObjectId);
        Check.True(BinaryPrimitives.ReadUInt32LittleEndian(drops.AsSpan(8)) == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(drops.AsSpan(12)) == 4451 &&
            !packets.Any(packet => packet.Length == 116 &&
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10027 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == boss.ObjectId),
            "late zero-HP corpse appearance gets the sparkling sack without native 10027 loot cleanup");

        f.Character.PositionX = corpse.X + WonderlandBossLootPolicy.InteractionRadius + 1;
        f.Registry.UpdateCharacter(f.Session, f.Character, advanceWorldRevision: false);
        Check.True(!f.Registry.TryResolveWonderlandBossLootPickup(f.Session, boss.ObjectId, 0, f.Now, out _),
            "loot cannot be taken beyond the corpse interaction range");
        f.MoveTo(corpse);
        f.Character.CurrentHp = 0;
        Check.True(!f.Registry.TryResolveWonderlandBossLootPickup(f.Session, boss.ObjectId, 0, f.Now, out _),
            "a dead player cannot take a boss sack");
        f.Character.CurrentHp = f.Character.MaxHp;
        f.Registry.MarkWonderlandBossLootClaimed(f.Session, f.Runtime.InstanceId, boss.ObjectId, boss.SpawnGeneration);
        Check.True(!f.Registry.TryResolveWonderlandBossLootPickup(f.Session, boss.ObjectId, 0, f.Now, out _),
            "a durable corpse claim suppresses the same per-boss runtime entitlement");
        var afterClaim = f.Transport.ReadLegacyPackets().Count;
        await f.Registry.RefreshWonderlandBossLootAsync(f.Session, CancellationToken.None);
        await f.FlushAsync();
        Check.True(f.Transport.ReadLegacyPackets().Skip(afterClaim).Any(packet => packet.Length == 16 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == Opcodes.MoveItem &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == boss.ObjectId),
            "claim refresh clears native loot UI through the bag-neutral10050 observer acknowledgment");
        Check.True(!f.Registry.TryResolveWonderlandBossLootPickup(f.Session, boss.ObjectId, 0, run.Deadline, out _),
            "an active run cannot claim past its deadline");
        var beforeExpiry = f.Transport.ReadLegacyPackets().Count;
        await f.Registry.AdvanceMonsterWorldOnceAsync(corpse.DespawnAt!.Value, CancellationToken.None);
        await f.FlushAsync();
        var expiredPackets = f.Transport.ReadLegacyPackets().Skip(beforeExpiry).ToArray();
        var cleanupIndex = Array.FindIndex(expiredPackets, packet => packet.SequenceEqual(
            PacketBuilder.MonsterDeathReward(boss.ObjectId, uint.MaxValue, 0, 0, 0)));
        var removalIndex = Array.FindIndex(expiredPackets, packet => packet.SequenceEqual(
            PacketBuilder.RemoveWorldObjects(boss.ObjectId)));
        Check.True(cleanupIndex >= 0 && removalIndex > cleanupIndex &&
            f.Runtime.Map.TryGetMonsterSnapshot(boss.ObjectId, out var retired) && !retired.IsSpawned,
            "expiry clears native loot before removing the corpse actor instead of leaving a permanent drop");
        var afterExpiry = f.Transport.ReadLegacyPackets().Count;
        await f.Registry.RefreshWonderlandBossLootAsync(f.Session, CancellationToken.None);
        await f.FlushAsync();
        Check.True(!f.Transport.ReadLegacyPackets().Skip(afterExpiry).Any(packet => packet.Length >= 8 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == Opcodes.MonsterDrops &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == boss.ObjectId),
            "an expired corpse's slot cannot be republished by a late refresh");
        Check.True(WonderlandBossLootPolicy.ForIsland(8, run.PartyCamp).Select(value => value.SackItemId)
                .SequenceEqual(new uint[] { 4458, 4459, 4460, 4461 }) &&
            WonderlandBossLootPolicy.ForIsland(5, 0).Single().SackItemId == 4455 &&
            WonderlandBossLootPolicy.ForIsland(5, 1).Single().SackItemId == 4454,
            "all final bosses and both opposing marshals retain their own original sacks");
    }
}
