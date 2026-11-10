using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string CheckName = "Wonderland real combat controls, buffs, reflection, and timed skills across both engines";

    public static async Task RunAsync()
    {
        CheckScopedAccuracyAndShapes();
        foreach (var monsterMode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var playerMode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckOpposedReductionAsync(monsterMode, playerMode);
            await CheckWonderlandActionGatesAsync(monsterMode, playerMode);
            await CheckBirdBuffAndCastAsync(monsterMode, playerMode);
            await CheckRockReflectionAndSilenceAsync(monsterMode, playerMode);
            await CheckPutridBirdAccuracyAsync(monsterMode, playerMode);
            await CheckAtlasDeathBlastAsync(monsterMode, playerMode);
            await CheckTerrainFireAsync(monsterMode, playerMode);
            foreach (var camp in new[] { GameDefaults.SpartaCamp, GameDefaults.AthensCamp })
                await CheckAlliedMarshalAsync(monsterMode, playerMode, camp);
            await CheckCapturedCommitAcrossTickAsync(monsterMode, playerMode);
            await CheckCompletedAtlasBlastAsync(monsterMode, playerMode);
            await CheckDelayedReadAndStaleAdvanceAsync(monsterMode, playerMode);
            await CheckSurvivingIslandCombatAsync(monsterMode, playerMode);
            await CheckNativePlayerDeathAsync(monsterMode, playerMode);
            await CheckBossCorpseLootAsync(monsterMode, playerMode);
        }
    }

    private static void CheckScopedAccuracyAndShapes()
    {
        var attacker = new CombatAttackerStats { Level = 130, Hit = 3000, PhysicalAttack = 10_000 };
        var boss = new CombatTargetStats { Level = 130, Dodge = 18_000, UsesDirectRatingAccuracy = true };
        var unbuffed = AuthoredCombatPveCurrent.ResolveBasicAttack(attacker, boss, 11);
        var buffed = AuthoredCombatPveCurrent.ResolveBasicAttack(attacker with { Hit = 28_000 }, boss, 11);
        Check.True(unbuffed.Rolls.HitChanceBasisPoints == 1500 && buffed.Rolls.HitChanceBasisPoints == 9800,
            "only explicitly scoped boss accuracy follows the authored15%-to98% bird mechanic");
        var ordinary = AuthoredCombatPveCurrent.ResolveBasicAttack(attacker, boss with { UsesDirectRatingAccuracy = false }, 11);
        Check.True(ordinary.Rolls.HitChanceBasisPoints != 1500, "normal PvE retains its prior hit formula");
        var cleave = new WonderlandBossAbility("Synthetic cone", 2000, false, 1,
            TimeSpan.Zero, TimeSpan.Zero, WonderlandAbilityShape.Frontal, Radius: 6);
        Check.True(WonderlandBossAbilityPolicy.InShape(cleave, 0, 0, 10, 0, 5, 0) &&
            !WonderlandBossAbilityPolicy.InShape(cleave, 0, 0, 10, 0, -5, 0) &&
            !WonderlandBossAbilityPolicy.InShape(cleave, 0, 0, 10, 0, 1, 8),
            "frontal attacks respect their fixed90-degree cone rather than hitting behind the boss");
    }

    private static async Task CheckOpposedReductionAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(2, monsters, players);
        foreach (var key in new[] { "derskey", "monkeyface" })
        {
            var monster = f.Monster(key);
            var target = f.Registry.AdjustPveMonsterTargetStats(f.Session, monster, f.Now, default);
            Check.True(target.PhysicalDamageReductionBasisPoints == (key == "derskey" ? 8000 : 0) &&
                target.MagicDamageReductionBasisPoints == (key == "monkeyface" ? 8000 : 0),
                "opposing island2 bosses apply80% reduction only to their authored damage channel");
            var before = f.Character.CurrentHp;
            await f.IncomingAsync(key);
            Check.True(f.Character.CurrentHp < before &&
                f.Registry.GetPlayerSkillCastControl(f.Session, f.Now) == PlayerSkillCastControl.None &&
                f.Registry.IsWonderlandNonSpellActionAllowed(f.Session, f.Now),
                "captured island-two basic attacks deal real damage without an invented fifty-percent stun");
            f.Now = f.Now.AddSeconds(3);
        }
    }

    private static async Task CheckBirdBuffAndCastAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(3, monsters, players);
        f.Character.CalculatedStats = Fixture.Stats(int.MaxValue);
        await f.IncomingAsync("petbird", hit: false);
        await f.IncomingAsync("petbird", hit: false);
        var aggregate = f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now);
        var offense = CombatCharacterStatsAdapter.ApplyRuntimeAttackerModifiers(
            CombatCharacterStatsAdapter.FromCharacter(f.Character), aggregate);
        Check.True(aggregate.AttackMultiplier == 5 && offense.PhysicalAttack == 50_000 && offense.MagicAttack == 50_000,
            "two dodged petbird attacks grant a single total5x attack buff, never25x");
        Check.Equal(1, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now.AddSeconds(15)).AttackMultiplier,
            "bird attack buff expires exactly at15seconds");
        f.Registry.ClearWonderlandPlayerEffects(f.Session);
        Check.Equal(1, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).AttackMultiplier,
            "same-map island departure explicitly removes the bird multiplier");
        f.Character.CalculatedStats = Fixture.Stats();
        f.MoveTo(f.Monster("rooster"));
        var before = f.Character.CurrentHp;
        var packetsBefore = f.Transport.ReadLegacyPackets().Count;
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
        var scheduled = f.CastDiagnostic("rooster");
        Check.Equal(before, f.Character.CurrentHp, "Fire Blast warning does not deal immediate damage");
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(1.19), CancellationToken.None);
        Check.Equal(before, f.Character.CurrentHp, "Fire Blast retains its full1.2second warning window");
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime,
            f.Now + WonderlandBossAbilityPolicy.For("rooster")[0].Windup, CancellationToken.None);
        await f.FlushAsync();
        Check.True(f.Transport.ReadLegacyPackets().Skip(packetsBefore).Any(packet =>
            packet.Length == 40 && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x2738 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 580),
            "the real scheduler publishes stock Fire Blast cast visuals");
        // Even a deterministic miss must emit its authoritative impact/damage result.
        Check.True(f.Transport.ReadLegacyPackets().Skip(packetsBefore).Any(packet => packet.Length == 30 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x272A),
            $"the scheduled cast executes through actual player-damage publication after windup; scheduled={scheduled}; after={f.CastDiagnostic("rooster")}");
    }

    private static async Task CheckRockReflectionAndSilenceAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(4, monsters, players);
        var before = f.Character.CurrentHp;
        var damage = f.DirectHit("rock", 400);
        Check.Equal(before - 40, f.Character.CurrentHp,
            "Rock reflects10% of actual committed direct damage synchronously before reward settlement");
        Check.Equal(400u, damage.BeforeHealth - damage.AfterHealth,
            "reflected damage cannot recursively hurt Rock even with player fixed rebound60879");
        await f.IncomingAsync("rock");
        Check.True(PlayerSkillCastControl.None == f.Registry.GetPlayerSkillCastControl(f.Session, f.Now),
            "the island4 boss is excluded from the normal monsters' silence effect");
        await f.IncomingAsync("support");
        Check.True(f.Registry.GetPlayerSkillCastControl(f.Session, f.Now) == PlayerSkillCastControl.Silenced &&
            f.Registry.IsWonderlandNonSpellActionAllowed(f.Session, f.Now),
            "island4 normal monster attacks silence spells while retaining basic attacks and potion use");
        f.Now = f.Now.AddSeconds(1);
        await f.IncomingAsync("support");
        Check.True(f.Registry.GetPlayerSkillCastControl(f.Session, f.Now.AddSeconds(4)) == PlayerSkillCastControl.None,
            "repeated captured silence refreshes four seconds instead of stacking durations");
    }

    private static async Task CheckPutridBirdAccuracyAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(7, monsters, players);
        await f.IncomingAsync("putridbird");
        await f.IncomingAsync("putridbird");
        Check.Equal(25_000, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).Hit,
            "Putrid Bird grants a refresh-only25000Hit bonus");
        var monster = f.Monster("multihead");
        var target = f.Registry.AdjustPveMonsterTargetStats(f.Session, monster, f.Now, default);
        var attacker = CombatCharacterStatsAdapter.ApplyRuntimeAttackerModifiers(
            CombatCharacterStatsAdapter.FromCharacter(f.Character), f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now));
        Check.Equal(9800, AuthoredCombatPveCurrent.ResolveBasicAttack(attacker, target, 123).Rolls.HitChanceBasisPoints,
            "actual scoped monster target and applied bird buff reach98% accuracy");
    }

    private static async Task CheckAtlasDeathBlastAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(8, monsters, players);
        var atlas = f.Monster("atlas");
        f.MoveTo(atlas);
        f.DirectHit("atlas", atlas.CurrentHealth);
        var before = f.Transport.ReadLegacyPackets().Count;
        var hp = f.Character.CurrentHp;
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(1.49), CancellationToken.None);
        Check.Equal(hp, f.Character.CurrentHp, "Atlas final blast preserves its1.5second death warning");
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(1.5), CancellationToken.None);
        await f.FlushAsync();
        var emitted = f.Transport.ReadLegacyPackets().Skip(before).Count(packet => packet.Length == 30 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x272A &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == atlas.ObjectId);
        Check.Equal(1, emitted, "the committed Atlas corpse emits exactly one final damage result");
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(1.5), CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(emitted, f.Transport.ReadLegacyPackets().Skip(before).Count(packet => packet.Length == 30 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x272A &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == atlas.ObjectId),
            "repeating the world tick cannot replay the dead Atlas blast");
    }
}
