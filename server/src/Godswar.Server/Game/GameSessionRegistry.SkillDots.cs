using Godswar.Server.Application.Finalise2;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // W2 skill statuses: a committed skill hit from one of the 16 damage-over-time skills
    // (SkillDamageOverTimeCatalog) may start that status on the surviving monster. Called
    // under the target's elemental gate, after the Medusa-ownership skip, so the tick
    // reuses the monster Burn pump (damage, kill credit, publish). Hot switches:
    // combat.skill_dots (0 = off, as before) and combat.skill_dot_chance (percent).
    // Dizzy (Steel Rain 4/5) and Concussed (Fire Blast 4/5): switch combat.skill_stuns, same chance rate.
    private static bool TryPlanPveMonsterSkillStun(PveElementalCommittedHit hit, out int durationMilliseconds)
    {
        durationMilliseconds = 0;
        if (hit.SkillId <= 0 ||
            !SkillHitStunCatalog.TryGet(hit.SkillId, out var statusId, out var duration) ||
            !Finalise2Rates.On(Finalise2RateKeys.SkillStuns))
        {
            return false;
        }

        var chanceBasisPoints = Math.Clamp(
            Finalise2Rates.Amount(Finalise2RateKeys.SkillDotChance), 0, 100) * 100;
        if (DeterministicCombatRandom.RollBasisPoints(
                hit.CombatEventId,
                Math.Max(0, hit.TargetOrder),
                CombatRandomStage.SkillStun) >= chanceBasisPoints)
        {
            return false;
        }

        durationMilliseconds = duration;
        Console.WriteLine(
            $"[skill-stun] status={statusId} skill={hit.SkillId} target={hit.DamageResult.ObjectId} {duration} ms");
        return true;
    }

    // Weakness (Ice Cone) and Blinded (Holy Light): switch combat.skill_debuffs, same chance rate.
    private static void TryApplyPveMonsterSkillDebuff(
        PveMonsterElementalState targetState,
        PveElementalCommittedHit hit,
        DateTimeOffset committedAt)
    {
        if (hit.SkillId <= 0 ||
            !SkillHitDebuffCatalog.TryGet(hit.SkillId, out var definition) ||
            !Finalise2Rates.On(Finalise2RateKeys.SkillDebuffs))
        {
            return;
        }

        var chanceBasisPoints = Math.Clamp(
            Finalise2Rates.Amount(Finalise2RateKeys.SkillDotChance), 0, 100) * 100;
        if (DeterministicCombatRandom.RollBasisPoints(
                hit.CombatEventId,
                Math.Max(0, hit.TargetOrder),
                CombatRandomStage.SkillDebuff) >= chanceBasisPoints)
        {
            return;
        }

        if (targetState.SkillDebuffs.TryApply(definition, committedAt.ToUnixTimeMilliseconds()))
        {
            Console.WriteLine(
                $"[skill-debuff] {definition.Kind} status={definition.StatusId} skill={definition.SkillId} target={hit.DamageResult.ObjectId} amount={definition.Amount} {definition.DurationMilliseconds} ms");
        }
    }

    private static void TryApplyPveMonsterSkillDot(
        PveMonsterElementalState targetState,
        long sourceCharacterId,
        PveElementalCommittedHit hit,
        DateTimeOffset committedAt)
    {
        if (hit.SkillId <= 0 ||
            !SkillDamageOverTimeCatalog.TryGet(hit.SkillId, out var definition) ||
            !Finalise2Rates.On(Finalise2RateKeys.SkillDots))
        {
            return;
        }

        var chanceBasisPoints = Math.Clamp(
            Finalise2Rates.Amount(Finalise2RateKeys.SkillDotChance), 0, 100) * 100;
        var roll = DeterministicCombatRandom.RollBasisPoints(
            hit.CombatEventId,
            Math.Max(0, hit.TargetOrder),
            CombatRandomStage.SkillDot);
        if (roll >= chanceBasisPoints)
        {
            return;
        }

        var percent = Math.Max(0, Finalise2Rates.Amount(Finalise2RateKeys.SkillDotDamagePercent));
        var tickDamage = (int)Math.Min(int.MaxValue, (long)definition.TickDamage * percent / 100);
        if (targetState.SkillDots.TryApply(
                definition,
                sourceCharacterId,
                hit.CombatEventId,
                committedAt.ToUnixTimeMilliseconds(),
                tickDamage))
        {
            Console.WriteLine(
                $"[skill-dot] {definition.Family} skill={definition.SkillId} source={sourceCharacterId} target={hit.DamageResult.ObjectId} tick={tickDamage} x{definition.TickCount} every {definition.IntervalMilliseconds} ms");
        }
    }
}
