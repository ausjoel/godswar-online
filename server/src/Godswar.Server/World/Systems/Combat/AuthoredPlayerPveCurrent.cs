namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Player attacks on monsters retain PvE accuracy and use PvP's critical ratio.
/// Version 5 changes the critical outcome policy, not attack or damage ratings.
/// </summary>
internal static class AuthoredPlayerPveCurrent
{
    public const int Version = 5;

    private static readonly AuthoredCombatFormula Formula = new(
        Version,
        AuthoredHitChancePolicy.Normalized(
            favorableAdjustmentBasisPoints: 4_000,
            dodgeAdjustmentBasisPoints: 4_000),
        AuthoredCombatV2.CriticalChancePolicy);

    public static CombatResolution ResolveBasicAttack(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        ulong eventId, int targetOrder = 0) =>
        Formula.ResolveBasicAttack(attacker, target, eventId, targetOrder);

    public static CombatResolution ResolveBasicAttackForOutcome(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        CombatHitOutcome outcome) =>
        Formula.ResolveBasicAttackForOutcome(attacker, target, outcome);

    public static CombatResolution ResolveSkillDamage(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        int property, decimal powerAdjustment, decimal flatPower,
        ulong eventId, int targetOrder = 0) =>
        Formula.ResolveSkillDamage(attacker, target, property, powerAdjustment,
            flatPower, eventId, targetOrder);

    public static CombatResolution ResolveSkillDamageForOutcome(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        int property, decimal powerAdjustment, decimal flatPower,
        CombatHitOutcome outcome) =>
        Formula.ResolveSkillDamageForOutcome(attacker, target, property,
            powerAdjustment, flatPower, outcome);

    public static int CalculateHitChanceBasisPoints(
        in CombatAttackerStats attacker, in CombatTargetStats target) =>
        Formula.CalculateHitChanceBasisPoints(attacker, target);

    public static int CalculateCriticalChanceBasisPoints(
        in CombatAttackerStats attacker, in CombatTargetStats target) =>
        Formula.CalculateCriticalChanceBasisPoints(attacker, target);
}
