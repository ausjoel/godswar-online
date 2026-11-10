using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerSkillDamageFormulaChecks
{
    public const string CheckName =
        "Historical Flame V3 capture formula replay";

    public static Task RunAsync()
    {
        CheckCapturePoints();
        CheckLiveMageForecast();
        CheckHitAndFixedProfile();
        CheckExplicitMitigation();
        CheckBoundedInputs();
        return Task.CompletedTask;
    }

    private static void CheckCapturePoints()
    {
        // Literal decimal predictions and observed packets are independently
        // recorded in flame-blast-level143-20260914/formula/candidate-validation.json.
        var points = new (int Seq, int Ma, int Bonus, int Rank, int Crit, int Flat,
            decimal DecimalDamage, uint Observed)[]
        {
            (12681, 79882, 8120, 24, 0, 0, 332120.574m, 332121),
            (16084, 7262, 8120, 24, 0, 0, 36048.834m, 36049),
            (17483, 7342, 8120, 24, 0, 0, 36374.994m, 36375),
            (17553, 7488, 8120, 24, 0, 0, 36970.236m, 36970),
            (17714, 7488, 8120, 25, 0, 0, 37228.446m, 37228),
            (24213, 80908, 8120, 25, 0, 0, 336561.786m, 336562),
            (60814, 7914, 10640, 23, 1010, 1740, 49959.413616m, 49960),
            (66587, 85504, 10640, 23, 1010, 1740, 446680.497576m, 446680),
            (101184, 6760, 10640, 23, 1010, 1740, 44058.960840m, 44059)
        };
        foreach (var point in points)
        {
            var attacker = Attacker(point.Ma, point.Bonus, point.Crit, point.Flat);
            var skill = Flame(point.Rank);
            var result = Landed(attacker, default, skill);
            Check.Equal(point.DecimalDamage, result.Evidence.DamageWithAppend,
                $"capture{point.Seq} retains the independent decimal prediction");
            Check.Equal((uint)decimal.Round(point.DecimalDamage, 0, MidpointRounding.AwayFromZero),
                result.Damage, $"capture{point.Seq} has explicit final rounding");
            Check.True(Math.Abs((long)result.Damage - point.Observed) <= 1,
                $"capture{point.Seq} prediction stays within the documented one-damage external rounding boundary");
            Check.Equal(result.Damage, CapturedFlameBlastV3.Preview(attacker, skill),
                "captured preview and admitted landed damage use the same profile");
            Check.True(result.FormulaVersion == 3 && result.IsCritical,
                "captured profile evidence is separately versioned");
        }
    }

    private static void CheckLiveMageForecast()
    {
        var attacker = Attacker(8560, 13540, 3000, 0) with
        {
            MagicAppendDamage = 3036,
            IgnoreMagicDefenseBasisPoints = 3200
        };
        var result = Landed(attacker, new() { MagicDefense = 2500 }, Flame(31));
        Check.Equal(72871.0125m, result.Evidence.DamageWithAppend,
            "AresMage's separate calculated forecast is preserved before rounding");
        Check.Equal(72871u, result.Damage, "AresMage forecast rounds to72871, not an external observation");
        var withoutOrdinaryDefenseOrAppend = Landed(attacker with { MagicAppendDamage = 0 },
            default, Flame(31));
        Check.Equal(result, withoutOrdinaryDefenseOrAppend,
            "captured profile excludes unsupported ordinary defense/append terms");
    }

    private static void CheckHitAndFixedProfile()
    {
        var attacker = Attacker(6760, 10640, 1010, 1740) with { Hit = 50, Critical = 0 };
        var target = new CombatTargetStats { Level = 143, Dodge = 500, CriticalResistance = 5000 };
        var skill = Flame(23);
        var hitCount = 0; var missCount = 0;
        for (ulong eventId = 1; eventId <= 128; eventId++)
        {
            var original = AuthoredCombatV1.ResolveSkillDamage(attacker, target, 1,
                skill.Power1, skill.Power2, eventId, 2);
            var actual = CapturedFlameBlastV3.Resolve(attacker, target, skill, eventId, 2);
            Check.True(original.Hit == actual.Hit &&
                       original.Rolls.HitChanceBasisPoints == actual.Rolls.HitChanceBasisPoints &&
                       original.Rolls.HitRollBasisPoints == actual.Rolls.HitRollBasisPoints,
                "captured profile preserves original deterministic Hit/Dodge and target ordering");
            Check.Equal(actual, CapturedFlameBlastV3.Resolve(attacker, target, skill, eventId, 2),
                "same input replays identical formula evidence");
            Check.Equal(actual, CapturedFlameBlastV3.Resolve(attacker with { Critical = int.MaxValue },
                target, skill, eventId, 2), "fixed captured outcome does not turn rating into a new random roll");
            if (actual.Hit)
            {
                hitCount++;
                Check.True(actual.IsCritical && actual.Rolls.CriticalRollBasisPoints == -1,
                    "landed captured profile has explicit fixed critical-style evidence");
            }
            else
            {
                missCount++;
                Check.True(actual.Damage == 0 && actual.CapturedDamageValue == uint.MaxValue,
                    "preserved miss cannot become positive damage");
            }
        }
        Check.True(hitCount > 0 && missCount > 0, "the deterministic cohort exercises hits and misses");
    }

    private static CombatAttackerStats Attacker(int ma, int bonus = 0, int crit = 0, int flat = 0) =>
        new() { Level = 143, Profession = 3, MagicAttack = ma, Hit = 1000000,
            MagicDamageBonusBasisPoints = bonus, CriticalDamageBasisPoints = crit,
            CriticalDamageFlat = flat };

    private static SkillCombatDefinition Flame(int rank) =>
        new(574, 63, 28, 11f, 4f, 1, 180, -0.5m, 180m + rank * 100m,
            ZodiacFlatPower: rank * 100m, ZodiacFlatRank: rank);

    private static CombatResolution Landed(in CombatAttackerStats attacker,
        in CombatTargetStats target, in SkillCombatDefinition skill)
    {
        for (ulong id = 1; id <= 256; id++)
        {
            var result = CapturedFlameBlastV3.Resolve(attacker, target, skill, id);
            if (result.Hit) return result;
        }
        throw new InvalidOperationException("Deterministic landed fixture was not found.");
    }
}
