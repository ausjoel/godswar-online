using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// W23 (the admin 2026-11-24: "Mobs [in Delphi Forest] are not doing much damage"): pins why a generated Lv146 monster
/// hit a geared same-level character for 1, and that the data fix (an attack-only gm_monster_attributes row, see
/// godswar-dev/tools/monster-damage and Development/build/release/w23) brings the hit into the retail band without
/// touching the monster's other ratings.
/// </summary>
/// <remarks>
/// Pure in-memory. The numbers are the rig's (2026-11-24, godswar_w23b): a Lv146 warrior in typical Q5 gear
/// (physical defence 1,442, magic defence 178, absorb 876, 28,155 HP) next to Delphi Forest's
/// C_normal_crocodilian_001. Before: 102 hits of 1. After: 33 hits of 643 = 2,961 - 1,442 - 876.
/// </remarks>
internal static class MonsterDamageBandChecks
{
    public const string CheckName = "W23 monster damage band: attack-only GM rows lift generated monsters";

    private const short Delphi = 20;
    private const string Crocodile = "C_normal_crocodilian_001";
    private const string PassiveSpy = "A_normals_greecewarrior_001";
    private const int RetailBandAttack = 2_961; // 2008 MonsterPhyAttack[146] 3,290 x MonsterObject.ini SkillCoe 0.9

    public static Task RunAsync()
    {
        try
        {
            CheckReborn146HitsForOne();
            CheckAttackOnlyRowLiftsTheHit();
            CheckPassiveTemplatesNeedTheirExclusion();
            CheckW25AttackSwitches();
        }
        finally
        {
            MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Empty);
        }

        return Task.CompletedTask;
    }

    private static CombatTargetStats TypicalWarrior146 => new()
    {
        Level = 146,
        PhysicalDefense = 1_442,
        MagicDefense = 178,
        Dodge = 24,
        PhysicalFlatAbsorption = 876,
        MagicFlatAbsorption = 876
    };

    private static CapturedMonsterSpawn Spawn(string key, uint objectId) =>
        Program.CreateCapturedMonster(objectId, 130f, 108f, key, tier: 146, maximumHealth: 80_000,
            mapId: Delphi, sceneKey: "Oracle_of_Delphi_All");

    private static uint NormalHit(in MonsterCombatProfile profile) =>
        AuthoredCombatPveCurrent.ResolveBasicAttackForOutcome(profile.ToAttackerStats(), TypicalWarrior146,
            CombatHitOutcome.Normal).Damage;

    // The root cause: the Reborn V1 attack is linear (21 + 3L + L/3 = 507 at Lv146), a geared Lv146 character's
    // defence plus absorb is 2,318, so every normal hit floors at 1. HP was recovered at retail size, attack was not.
    private static void CheckReborn146HitsForOne()
    {
        MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Empty);
        var profile = MonsterCombatProfileCatalog.Empty.Resolve(Spawn(Crocodile, 46_360));
        Check.Equal(146, profile.Level, "the spawn level drives the V1 profile");
        Check.Equal(507, profile.PhysicalAttack, "V1 physical attack at Lv146");
        Check.Equal(1u, NormalHit(profile), "a V1-only Lv146 monster hits a typical Lv146 character for the floor of 1");
    }

    private static void CheckAttackOnlyRowLiftsTheHit()
    {
        MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Empty);
        var v1 = MonsterCombatProfileCatalog.Empty.Resolve(Spawn(Crocodile, 46_360));
        MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Create([], [],
            [((Delphi, Crocodile), new MonsterAttributeOverride(
                PhysicalAttack: RetailBandAttack, MagicAttack: RetailBandAttack))]));
        var lifted = MonsterCombatProfileCatalog.Empty.Resolve(Spawn(Crocodile, 46_360));
        Check.Equal(v1 with { PhysicalAttack = RetailBandAttack, MagicAttack = RetailBandAttack }, lifted,
            "an attack-only row changes attack and nothing else (level, defences, hit, dodge, crit stay V1)");
        var hit = NormalHit(lifted);
        Check.Equal(643u, hit, "the lifted hit is attack - defence - absorb, exactly as the rig measured");
        Check.True(hit * 100 >= 28_155u * 2 && hit * 100 <= 28_155u * 10,
            "a same-level normal monster takes 2-10% of a typical character's HP per hit");

        var elsewhere = MonsterCombatProfileCatalog.Empty.Resolve(
            Program.CreateCapturedMonster(46_361, 0f, 0f, Crocodile, tier: 146, mapId: 9, sceneKey: "Thebes_All"));
        Check.Equal(507, elsewhere.PhysicalAttack, "the row is per map: the same template on another map keeps V1");
    }

    // tools/monster-damage/audit.py skips passive templates: a GM attack row is applied after the passive clamp
    // (MonsterCombatProfileCatalog.Resolve), so a row would turn Athens' Spartan spies into real attackers.
    private static void CheckPassiveTemplatesNeedTheirExclusion()
    {
        MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Empty);
        Check.Equal(1, MonsterCombatProfileCatalog.Empty.Resolve(Spawn(PassiveSpy, 46_362)).PhysicalAttack,
            "a passive template hits for 1 without a row");
        MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Create([], [],
            [((Delphi, PassiveSpy), new MonsterAttributeOverride(PhysicalAttack: RetailBandAttack))]));
        Check.Equal(RetailBandAttack, MonsterCombatProfileCatalog.Empty.Resolve(Spawn(PassiveSpy, 46_362)).PhysicalAttack,
            "a GM attack row overrides the passive clamp, which is why the W23 fix leaves passive templates out");
    }

    // W25 (the admin d152b): one hot switch per reversible attack data step; off = the attack from before the step.
    private static void CheckW25AttackSwitches()
    {
        var defs = Godswar.Server.Application.Finalise2.Finalise2Rates.Definitions;
        decimal Default(string key) => defs.Single(d => d.Key == key).DefaultValue;
        Check.True(Default(MonsterAttackSwitches.W23NewMaps) == 1m && Default(MonsterAttackSwitches.W25WorldBosses) == 1m &&
                   Default(MonsterAttackSwitches.SuggestedLowLevel) == 0m, "W25: W23 and world-boss switches on, suggested off by default");
        var raised = new MonsterAttributeOverride(PhysicalAttack: 2961, MagicAttack: 2961, AttackStep: MonsterAttributeOverride.W23NewMapsStep,
            OffPhysicalAttack: 800, OffMagicAttack: 900);
        var created = new MonsterAttributeOverride(PhysicalAttack: 6417, MagicAttack: 6417, AttackStep: MonsterAttributeOverride.W25WorldBossStep);
        var plain = new MonsterAttributeOverride(PhysicalAttack: 1234);
        try
        {
            Check.Equal(2961, raised.WithAttackSwitches().PhysicalAttack ?? -1, "W25: switch on keeps the W23 attack");
            Godswar.Server.Application.Rates.ServerRates.Install(Godswar.Server.Application.Rates.ServerRatesSnapshot.Create(
                [KeyValuePair.Create(MonsterAttackSwitches.W23NewMaps, 0m), KeyValuePair.Create(MonsterAttackSwitches.W25WorldBosses, 0m)],
                1, DateTimeOffset.UtcNow, "check", "test"));
            Check.True(raised.WithAttackSwitches() is { PhysicalAttack: 800, MagicAttack: 900 }, "W25: W23 off puts the pre-W23 attack back");
            Check.True(created.WithAttackSwitches() is { PhysicalAttack: null, MagicAttack: null }, "W25: a row the step created falls back to the formula");
            Check.Equal(1234, plain.WithAttackSwitches().PhysicalAttack ?? -1, "W25: a GM row no step wrote is untouched");
            Check.Equal(800, raised.Overlay(MonsterAttributeOverride.None).WithAttackSwitches().PhysicalAttack ?? -1, "W25: overlay keeps the step");
        }
        finally
        {
            Godswar.Server.Application.Rates.ServerRates.ResetToDefaults();
        }
    }
}
