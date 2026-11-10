using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static class FlameBlastPulsePolicy
{
    // The client defines the field with EffectTurn=10 and TimeInterval=1: one
    // damage pass per second for ten seconds after the accepted cast. Reference
    // server traffic agrees: an isolated rank-I field retargeted every
    // 0.99-1.14 seconds (best fit 1.06 s) with its first pass about one second
    // after the cast, and a field kept ticking past eleven seconds. The earlier
    // +4/+8/+12/+16 interpretation measured the caster's recast cadence, not
    // the field tick. Ranks I-V share this family policy.
    public const int TotalPulses = 10;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    public static bool AppliesTo(in SkillCombatDefinition combat) =>
        combat.SkillId is >= 570 and <= 574 &&
        SkillCombatResolver.IsHostileMonsterGroundAreaSkill(combat);
}
