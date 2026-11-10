using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct PlayerSkillDamageInputs(
    uint SkillId, int Property, decimal Power1, decimal Power2,
    decimal ZodiacFlatPower, int ZodiacFlatRank, decimal ZodiacPowerAdjustment,
    int Target, int Affect, float Distance, float Range)
{
    public decimal AuthoredPower1 => Power1 - ZodiacPowerAdjustment;
    public decimal AuthoredPower2 => Power2 - ZodiacFlatPower;

    public bool HasHostileShape =>
        PlayerSkillDamageEligibility.IsDamaging(Target, Affect, Distance, Range, Property, 0m, 0m);

    public bool HasValidProjection
    {
        get
        {
            if (SkillId > int.MaxValue || ZodiacFlatRank is < 0 or > 50 ||
                ZodiacFlatPower < 0m || ZodiacFlatPower > Power2 ||
                ZodiacPowerAdjustment is < 0m or > 1.2m) return false;
            try
            {
                var damaging = PlayerSkillDamageEligibility.IsDamaging(
                    Target, Affect, Distance, Range, Property, AuthoredPower1, AuthoredPower2);
                return ZodiacFlatPower == ZodiacFlatRank * (damaging ? 95m : 100m);
            }
            catch (OverflowException) { return false; }
        }
    }

    public bool HasAuthoredDamage => AuthoredPower1 > -1m || AuthoredPower2 > 0m;

    public static PlayerSkillDamageInputs From(in SkillCombatDefinition skill) =>
        new(unchecked((uint)skill.SkillId), skill.Property, skill.Power1, skill.Power2,
            skill.ZodiacFlatPower, skill.ZodiacFlatRank, skill.ZodiacPowerAdjustment,
            skill.Target, skill.AffectObj, skill.Distance, skill.Range);

    public static PlayerSkillDamageInputs From(in PlayerCombatSkillSnapshot skill) =>
        new(skill.SkillId, skill.Property, skill.Power1, skill.Power2,
            skill.ZodiacFlatPower, skill.ZodiacFlatRank, skill.ZodiacPowerAdjustment,
            skill.Target, skill.AffectObject, skill.Distance, skill.AreaRadius);
}
