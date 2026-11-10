using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckSurvivingIslandCombatAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using (var f = await Fixture.CreateAsync(1, monsters, players))
        {
            var raider = f.Monster("raider");
            var tower = f.Monster("tower");
            f.MoveTo(raider);
            var packetCount = f.Transport.ReadLegacyPackets().Count;
            f.Now = f.Now.AddTicks(1);
            f.DirectHit("alpha", f.Monster("alpha").CurrentHealth);
            f.Runtime.Map.TryGetWonderlandSnapshot(out var cleared);
            Check.True(cleared.CurrentIsland == 2 && cleared.PublicationPending &&
                f.Monsters().Where(m => m.ObjectId == raider.ObjectId || m.ObjectId == tower.ObjectId)
                    .All(m => m.IsAlive && m.IsSpawned),
                "Alpha's death unlocks island two while its Raiders and towers remain alive and visible");
            await f.IncomingAsync("raider", island: 1);
            await f.FlushAsync();
            Check.True(f.Transport.ReadLegacyPackets().Skip(packetCount).Any(packet => packet.Length == 32 &&
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x272A &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == raider.ObjectId &&
                packet[28] == 1 && packet[30] == 0 && packet[31] == 0),
                "a surviving Raider still attacks through the captured native animation after Alpha dies");
            CheckBirdVisualPrefix(f.Transport.ReadLegacyPackets().Skip(packetCount), raider.ObjectId,
                0x1448, f.Character.PositionX, f.Character.PositionZ);
            Check.True(f.Runtime.Map.TrySpawnPendingWonderlandStage(f.Now, out _), "the next island publishes independently");
            var before = f.Character.CurrentHp;
            await f.IncomingAsync("tower", island: 1);
            Check.True(f.Character.CurrentHp < before, "an earlier-island tower still deals its real basic attack");
            var assaulter = f.Monster("assaulter", island: 1);
            before = f.Character.CurrentHp;
            await f.IncomingAsync("assaulter", island: 1);
            Check.True(f.Character.CurrentHp < before &&
                f.TryDirectHit(assaulter, assaulter.CurrentHealth, out var assaulterDeath) && assaulterDeath.Killed,
                "the captured pet-marked Assaulter model attacks and receives player damage as a hostile monster");
            f.Runtime.Map.TryGetWonderlandSnapshot(out var progress);
            Check.True(f.TryDirectHit(raider, raider.CurrentHealth, out var death) && death.Killed,
                "an admitted player can legitimately kill a remaining earlier-island mob");
            f.Runtime.Map.TryGetWonderlandSnapshot(out var after);
            Check.True(after.CurrentIsland == progress.CurrentIsland && after.CompletedIslands == progress.CompletedIslands &&
                after.RequiredMonstersRemaining == progress.RequiredMonstersRemaining && after.Clears.SequenceEqual(progress.Clears),
                "late support deaths do not alter the next island's boss count or award another clear");
        }

        await using (var f = await Fixture.CreateAsync(3, monsters, players))
        {
            await f.IncomingAsync("petbird", hit: false);
            f.DirectHit("rooster", f.Monster("rooster").CurrentHealth);
            Check.Equal(5, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).AttackMultiplier,
                "defeating Rooster does not erase the bird buff from players still on that island");
            Check.True(f.Runtime.Map.TrySpawnPendingWonderlandStage(f.Now, out _), "island four publishes");
            await f.IncomingAsync("petbird", hit: false, island: 3);
            Check.Equal(5, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).AttackMultiplier,
                "remaining petbirds still refresh their support mechanic after the next island publishes");
        }

        await using (var f = await Fixture.CreateAsync(4, monsters, players))
        {
            f.DirectHit("rock", f.Monster("rock").CurrentHealth);
            Check.True(f.Runtime.Map.TrySpawnPendingWonderlandStage(f.Now, out _), "island five publishes");
            await f.IncomingAsync("support", island: 4);
            Check.True(f.Registry.GetPlayerSkillCastControl(f.Session, f.Now) == PlayerSkillCastControl.Silenced,
                "earlier-island supports retain their silence mechanic after Rock dies");
        }
    }
}
