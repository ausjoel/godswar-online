using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckDelayedReadAndStaleAdvanceAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using (var f = await Fixture.CreateAsync(2, monsters, players))
        {
            await f.ApplySyntheticEncounterStunAsync();
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(.5), CancellationToken.None);
            var stats = f.Registry.AdjustPveMonsterTargetStats(f.Session, f.Monster("derskey"), f.Now, default);
            Check.Equal(8000, stats.PhysicalDamageReductionBasisPoints,
                "a delayed target query preserves Derskey reduction after a newer world tick");
            Check.True(f.Registry.GetPlayerSkillCastControl(f.Session, f.Now) == PlayerSkillCastControl.Stunned,
                "a delayed control query cannot bypass the currently active stun");
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(2), CancellationToken.None);
            Check.True(f.Registry.GetPlayerSkillCastControl(f.Session, f.Now) == PlayerSkillCastControl.None,
                "a delayed control query cannot resurrect stun after authoritative expiry");
        }
        await using (var f = await Fixture.CreateAsync(3, monsters, players))
        {
            f.Character.CalculatedStats = Fixture.Stats(int.MaxValue);
            await f.IncomingAsync("petbird", hit: false);
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(.5), CancellationToken.None);
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
            Check.Equal(5, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).AttackMultiplier,
                "a stale world tick preserves the current bird buff and delayed reads still see it");
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(15), CancellationToken.None);
            Check.Equal(1, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).AttackMultiplier,
                "delayed reads cannot resurrect a bird buff past the accepted world time");
        }
        await using (var f = await Fixture.CreateAsync(7, monsters, players))
        {
            await f.IncomingAsync("putridbird");
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(.5), CancellationToken.None);
            var stats = f.Registry.AdjustPveMonsterTargetStats(f.Session, f.Monster("multihead"), f.Now, default);
            Check.True(stats.UsesDirectRatingAccuracy && stats.Dodge == 18_000 &&
                f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).Hit == 25_000,
                "delayed Multi-head target and player reads retain scoped accuracy and the active Hit buff");
        }
        await using (var f = await Fixture.CreateAsync(8, monsters, players))
        {
            var atlas = f.Monster("atlas");
            f.DirectHit("atlas", atlas.CurrentHealth);
            var packets = f.Transport.ReadLegacyPackets().Count;
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddTicks(-1), CancellationToken.None);
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime,
                f.Now + WonderlandBossAbilityPolicy.AtlasDeathBlast.Windup, CancellationToken.None);
            await f.FlushAsync();
            Check.Equal(1, f.Transport.ReadLegacyPackets().Skip(packets).Count(packet => IsDamagePacket(packet) &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == atlas.ObjectId),
                "a stale tick cannot erase the already committed Atlas death blast");
        }
    }
}
