using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckTerrainFireAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(6, monsters, players);
        f.Runtime.Map.TryGetWonderlandSnapshot(out var run);
        var approach = run.Geometry.GroundFirePositions.First(point =>
            WonderlandTerrainPolicy.DistanceSquared(run.ActiveSpawns[0].Position, point.X, point.Z) > 24 * 24);
        MovePlayer(f, approach.X, approach.Z);
        var beforePackets = f.Transport.ReadLegacyPackets().Count;
        var hp = f.Character.CurrentHp;
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
        await f.FlushAsync();
        var warnings = f.Transport.ReadLegacyPackets().Skip(beforePackets).Where(IsCastWarning).ToArray();
        Check.Equal(3, warnings.Length, "terrain schedule admits exactly three real fire warnings away from the boss");
        var points = warnings.Select(p => (X: BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(32)),
            Z: BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(36)))).ToArray();
        Check.True(points.Distinct().Count() == 3 && points.All(point =>
            run.Geometry.GroundFirePositions.Any(p => p.X == point.X && p.Z == point.Z) &&
            WonderlandTerrainPolicy.DistanceSquared(run.Entrance, point.X, point.Z) > 15 * 15 &&
            WonderlandTerrainPolicy.DistanceSquared(run.Exit, point.X, point.Z) > 15 * 15),
            "every native fire warning keeps its entire five-unit circle outside both ten-unit travel safe zones");
        var boss = f.Monster("platinum");
        Check.True(warnings.All(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 589) &&
            f.Runtime.Map.IsMonsterVisibleTo(f.Session, boss.ObjectId, boss.SpawnGeneration),
            "ground warnings use the native coordinate-target skill and hydrate their real dragon source");
        var occupied = points.First(point =>
            (point.X - boss.X) * (point.X - boss.X) + (point.Z - boss.Z) * (point.Z - boss.Z) > 100);
        MovePlayer(f, occupied.X, occupied.Z);
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(1.49), CancellationToken.None);
        Check.Equal(hp, f.Character.CurrentHp, "terrain fire preserves its full1.5second reaction window");
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(1.5), CancellationToken.None);
        await f.FlushAsync();
        Check.True(f.Transport.ReadLegacyPackets().Skip(beforePackets).Any(IsDamagePacket),
            "standing in a warned circle executes an actual authoritative player damage result");
        var impacts = f.Transport.ReadLegacyPackets().Skip(beforePackets).Where(IsGroundFireImpact).ToArray();
        Check.True(impacts.Length == 3 && impacts.All(packet =>
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == boss.ObjectId &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 0) &&
            impacts.Select(packet => (BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(16)),
                BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(20)))).ToHashSet().SetEquals(points),
            "each warned circle detonates once at its ground coordinates, including the two circles with no victim");
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(2.99), CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(3, f.Transport.ReadLegacyPackets().Skip(beforePackets).Count(IsCastWarning),
            "ground fire cannot schedule another group before three seconds");
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(3), CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(6, f.Transport.ReadLegacyPackets().Skip(beforePackets).Count(IsCastWarning),
            "ground fire schedules the next three circles exactly at three seconds");
        var hits = f.Transport.ReadLegacyPackets().Count(IsDamagePacket);
        f.Runtime.Map.CancelWonderland(f.Now.AddSeconds(3.1));
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(4.5), CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(hits, f.Transport.ReadLegacyPackets().Count(IsDamagePacket),
            "termination cancels terrain fire already waiting to detonate");
        Check.Equal(3, f.Transport.ReadLegacyPackets().Skip(beforePackets).Count(IsGroundFireImpact),
            "termination also cancels pending ground-fire visuals");
    }

    private static async Task CheckAlliedMarshalAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players, byte camp)
    {
        await using var f = await Fixture.CreateAsync(5, monsters, players, camp);
        f.Runtime.Map.TryGetWonderlandSnapshot(out var run);
        var allyPolicy = run.ActiveSpawns.Single(p => p.IsAllied && p.IsBoss);
        var enemyPolicy = run.ActiveSpawns.Single(p => !p.IsAllied && p.IsBoss);
        Check.Equal(8_500_000u, f.Monsters().Single(m => m.ObjectId == allyPolicy.ObjectId).MaximumHealth,
            "the admitted faction's helping marshal retains its original captured HP");
        Check.Equal(42_500_000u, f.Monsters().Single(m => m.ObjectId == enemyPolicy.ObjectId).MaximumHealth,
            "the opposing marshal has five times its original HP");
        var world = f.Runtime.Map.InitializeMonsters([], f.Now);
        var enemyBefore = f.Monsters().Single(m => m.ObjectId == enemyPolicy.ObjectId).CurrentHealth;
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
        Check.Equal(enemyBefore, f.Monsters().Single(m => m.ObjectId == enemyPolicy.ObjectId).CurrentHealth,
            "allied marshal cannot damage the distant enemy across the island");
        var inRange = false;
        for (var i = 0; i < 800 && !inRange; i++)
        {
            var ally = f.Monsters().Single(m => m.ObjectId == allyPolicy.ObjectId);
            var enemy = f.Monsters().Single(m => m.ObjectId == enemyPolicy.ObjectId);
            var dx = ally.X - enemy.X;
            var dz = ally.Z - enemy.Z;
            var distance = MathF.Sqrt(dx * dx + dz * dz);
            inRange = distance <= allyPolicy.Stats.AttackRange;
            if (inRange) break;
            // First acquire nearby, then lead the enemy just beyond the ally.
            var step = Math.Min(12, distance + 1);
            var x = enemy.X + dx / distance * step;
            var z = enemy.Z + dz / distance * step;
            f.Now += MonsterMapRuntime.TickInterval;
            world.Advance(f.Now, [new(f.Character.Id, x, z, true, WorldInstanceId: f.Runtime.InstanceId)]);
        }
        Check.True(inRange, "the real monster engine can lure the hostile marshal into allied helping reach");
        var nearAlly = f.Monsters().Single(m => m.ObjectId == allyPolicy.ObjectId);
        var nearEnemy = f.Monsters().Single(m => m.ObjectId == enemyPolicy.ObjectId);
        Check.True((nearAlly.X - nearEnemy.X) * (nearAlly.X - nearEnemy.X) +
            (nearAlly.Z - nearEnemy.Z) * (nearAlly.Z - nearEnemy.Z) > 3 * 3,
            "the ally responds before the hostile marshal reaches the former three-unit range");
        var enemyDistanceSquared = (nearAlly.X - nearEnemy.X) * (nearAlly.X - nearEnemy.X) +
            (nearAlly.Z - nearEnemy.Z) * (nearAlly.Z - nearEnemy.Z);
        Check.True(f.Monsters().Any(monster => monster.IsAlive && monster.IsSpawned &&
                f.Runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var policy) &&
                policy.Stage == 5 && !policy.IsAllied && !policy.IsBoss &&
                (nearAlly.X - monster.X) * (nearAlly.X - monster.X) +
                (nearAlly.Z - monster.Z) * (nearAlly.Z - monster.Z) < enemyDistanceSquared),
            "the live lure leaves a closer hostile soldier available to distract a nearest-only targeting policy");
        var rewards = 0;
        f.Registry.RegisterPveMonsterKillRewardPreparer(f.Session, _ =>
        {
            rewards++;
            return Task.FromResult<PreparedPveMonsterKillReward?>(null);
        });
        f.MoveTo(f.Monsters().Single(m => m.ObjectId == allyPolicy.ObjectId));
        await using (var visible = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
            f.Character.PositionX, f.Character.PositionZ, CancellationToken.None)) visible!.Commit();
        var presentationStart = f.Transport.ReadLegacyPackets().Count;
        for (var i = 0; i < 20 && f.Monsters().Single(m => m.ObjectId == enemyPolicy.ObjectId).CurrentHealth == enemyBefore; i++)
        {
            f.Now = f.Now.AddSeconds(2);
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
        }
        await f.FlushAsync();
        var aidPackets = f.Transport.ReadLegacyPackets().Skip(presentationStart).Where(p =>
            p.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == allyPolicy.ObjectId).ToArray();
        Check.True(aidPackets.Where(p => p.Length == 24 && BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == 10046)
            .Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(12))).Take(2).SequenceEqual([2004u, 2000u]) &&
            aidPackets.Any(IsDamagePacket) && aidPackets.All(p => !IsCastWarning(p)) &&
            aidPackets.Where(IsDamagePacket).All(p => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(20)) == enemyPolicy.ObjectId),
            "allied aid targets the opposing marshal with the captured 2004/2000 pair despite surviving hostile supports");
        var allyName = f.Monsters().Single(monster => monster.ObjectId == allyPolicy.ObjectId).Definition.DisplayName;
        Check.True(f.Transport.ReadLegacyPackets().Where(IsRedBossNotice).All(packet =>
                !ReadRedNoticeText(packet).Contains(allyName, StringComparison.Ordinal)),
            "allied basic aid does not produce a hostile red danger announcement");
        await CheckMarshalHealthPublicationAsync(f, enemyPolicy.ObjectId, allyPolicy.ObjectId);
        var afterBasic = f.Monsters().Single(m => m.ObjectId == enemyPolicy.ObjectId).CurrentHealth;
        f.Now += allyPolicy.Stats.AttackInterval;
        f.ApplySyntheticAlliedStun(allyPolicy.ObjectId);
        var blockedStart = f.Transport.ReadLegacyPackets().Count;
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(1), CancellationToken.None);
        await f.FlushAsync();
        var blockedPackets = f.Transport.ReadLegacyPackets().Skip(blockedStart).ToArray();
        var otherAlliedIds = run.ActiveSpawns.Where(policy => policy.IsAllied && policy.ObjectId != allyPolicy.ObjectId)
            .Select(policy => policy.ObjectId).ToHashSet();
        var soldierDamage = blockedPackets.Where(packet => IsDamagePacket(packet) &&
                otherAlliedIds.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4))) &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20)) == enemyPolicy.ObjectId)
            .Sum(packet => (long)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24)));
        Check.True(f.Monsters().Single(m => m.ObjectId == allyPolicy.ObjectId).ControlsAt(f.Now.AddSeconds(1))
                .HasFlag(Godswar.Server.State.HostileStatusControlFlags.NonAttackUsing) &&
            blockedPackets.All(packet => packet.Length < 8 ||
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) != allyPolicy.ObjectId ||
                !(IsDamagePacket(packet) || BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10046)),
            "the otherwise-ready stunned marshal emits neither its native skill pair nor basic damage");
        Check.Equal(soldierDamage, (long)afterBasic - f.Monsters().Single(m => m.ObjectId == enemyPolicy.ObjectId).CurrentHealth,
            "any concurrent enemy HP loss is exactly the unstunned allied soldiers' separate native hits");
        Check.True(f.Monsters().Single(m => m.ObjectId == enemyPolicy.ObjectId).CurrentHealth < enemyBefore,
            "the allied marshal applies real monster-v-monster aid after the enemy is lured into reach");
        Check.Equal(0, rewards, "NPC aid cannot create a fictitious player reward request");
        var allyCurrent = f.Monsters().Single(m => m.ObjectId == allyPolicy.ObjectId);
        Check.True(!f.Runtime.Map.TryApplyMonsterDamageGuarded(allyCurrent.ObjectId, 1, f.Character.Id,
            allyCurrent.SpawnGeneration, allyCurrent.HealthRevision, f.Now, out _),
            "the assisting marshal remains immune to player farming");
    }

    private static void MovePlayer(Fixture f, float x, float z)
    {
        f.Character.PositionX = x;
        f.Character.PositionZ = z;
        f.Registry.UpdateCharacter(f.Session, f.Character, advanceWorldRevision: false);
    }

    private static bool IsCastWarning(byte[] packet) => packet.Length == 40 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x2738;
    private static bool IsDamagePacket(byte[] packet) => packet.Length is 30 or 32 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x272A;
}
