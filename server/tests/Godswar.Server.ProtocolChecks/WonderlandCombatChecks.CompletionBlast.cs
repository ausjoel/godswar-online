using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckCompletedAtlasBlastAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(8, monsters, players);
        f.Runtime.Map.TryGetWonderlandSnapshot(out var run);
        var final = run.ActiveSpawns.Last(p => p.MechanicKey == "atlas");
        foreach (var policy in run.ActiveSpawns.Where(p => p.IsBoss))
        {
            var current = f.Monsters().Single(m => m.ObjectId == policy.ObjectId);
            f.Now = f.Now.AddMilliseconds(1);
            Check.True(f.TryDirectHit(current, current.CurrentHealth, out _),
                "all four final-island bosses commit through registry damage");
        }
        var last = f.Monsters().Single(m => m.ObjectId == final.ObjectId);
        f.Now = f.Now.AddMilliseconds(1);
        Check.True(f.TryDirectHit(last, last.CurrentHealth, out var death) && death.Killed,
            "an optional Atlas commits lethal damage just before the chest guard");
        f.Runtime.Map.TryGetWonderlandSnapshot(out run);
        Check.True(run.State == WonderlandRunState.Active && run.RequiredMonstersRemaining == 1,
            "the optional Atlas death cannot complete the run");
        var guard = f.Monsters().Single(m => m.ObjectId == run.ActiveSpawns.Single(p =>
            p.Role == WonderlandMonsterRole.ChestGuard).ObjectId);
        f.Now = f.Now.AddMilliseconds(1);
        Check.True(f.TryDirectHit(guard, guard.CurrentHealth, out _), "the unlocked chest guard commits lethal damage");
        f.Runtime.Map.TryGetWonderlandSnapshot(out run);
        Check.True(run.State == WonderlandRunState.Completed, "the chest guard completes the actual run");
        var profile = f.Registry.AdjustPveMonsterAttackerProfile(f.Session, death.Monster, f.Now,
            f.Registry.GameplayCatalogs.MonsterCombatProfiles.Resolve(death.Monster.Definition));
        Check.Equal(16_000, profile.MagicAttack,
            "a committed corpse retains authored Atlas magic attack during completion countdown");
        // The hit fixture moved the player to the guard. Return to the Atlas
        // before resolving its pending blast so this still tests an in-range hit.
        f.MoveTo(death.Monster);
        var before = f.Transport.ReadLegacyPackets().Count;
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime,
            f.Now + WonderlandBossAbilityPolicy.AtlasDeathBlast.Windup, CancellationToken.None);
        await f.FlushAsync();
        var finalPackets = f.Transport.ReadLegacyPackets().Skip(before).Where(packet => IsDamagePacket(packet) &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == final.ObjectId).ToArray();
        Check.Equal(1, finalPackets.Length,
            "the optional Atlas executes one native death-blast result after run completion");
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(finalPackets[0].AsSpan(24));
        // Which other corpses cover the player can change event allocation.
        // Assert the exact amount for the native outcome, including a valid
        // authored 50% critical bonus, instead of assuming a noncritical roll.
        var expectedDamage = (CombatHitOutcome)finalPackets[0][29] switch
        {
            CombatHitOutcome.Normal => 48_000u,
            CombatHitOutcome.Critical => 72_000u,
            CombatHitOutcome.Miss => uint.MaxValue,
            _ => throw new InvalidOperationException("Atlas emitted an unknown native damage outcome.")
        };
        Check.Equal(expectedDamage, damage,
            "completed Atlas matches its native outcome:16000x3 magic,150% critical,or explicit miss");
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime,
            f.Now + WonderlandBossAbilityPolicy.AtlasDeathBlast.Windup + TimeSpan.FromSeconds(2), CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(1, f.Transport.ReadLegacyPackets().Skip(before).Count(packet => IsDamagePacket(packet) &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == final.ObjectId),
            "completion countdown cannot repeat or replace a committed Atlas death blast");
    }
}
