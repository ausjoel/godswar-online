using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// PROF (q06): Alchemy Sigil Stones deal exactly the damage their item text names ("Deals 1800 damage to
/// target's HP"): no hit roll, no critical, no defence, no elemental adjustment. Off unless
/// GODSWAR_SIGIL_FIXED_DAMAGE=1; then the stones still go through the normal formula, as before.
/// Only the combat definition SigilStonePolicy builds matches: skills 4877-4888 with Power1 = -1 and no MP cost
/// (Magic.ini has Power1 = 0 for these skills, so a client casting the skill directly never matches).
/// </summary>
internal static class SigilFixedDamage
{
    public const string EnabledVariable = "GODSWAR_SIGIL_FIXED_DAMAGE";

    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable(EnabledVariable) == "1";

    public static bool TryGet(in PlayerCombatSkillSnapshot skill, out uint damage)
    {
        damage = 0;
        if (!Enabled || skill.SkillId is < 4877 or > 4888 || skill.Power1 != -1m || skill.ManaCost != 0 ||
            skill.Power2 is < 1m or > 1_000_000m)
        {
            return false;
        }

        damage = (uint)skill.Power2;
        return true;
    }

    /// <summary>The formula's resolution with the outcome forced to a normal hit of the fixed amount.</summary>
    public static CombatResolution Apply(in CombatResolution formula, uint damage) =>
        formula with { Outcome = CombatHitOutcome.Normal, Damage = damage };
}
