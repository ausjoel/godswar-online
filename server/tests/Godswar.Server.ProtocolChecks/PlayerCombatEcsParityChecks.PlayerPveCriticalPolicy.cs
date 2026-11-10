using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsParityChecks
{
    public const string PlayerPveCriticalCheckName =
        "Player PvE critical ratio matches PvP without changing hit or incoming damage policy";

    public static Task RunPlayerPveCriticalPolicyAsync()
    {
        CheckPlayerPveCriticalRatings();
        CheckPlayerPveCriticalRolls();
        CheckPlayerPveBasicAdapters();
        CheckPlayerPveSkillAdapters();
        return Task.CompletedTask;
    }

    private static void CheckPlayerPveCriticalRatings()
    {
        var points = new (int Critical, int Resistance, int Chance)[]
        {
            (200, 100, 6666), (100, 200, 3333), (100, 100, 5000),
            (0, 100, 0), (0, 0, 0), (-1, 100, 0), (-1, -1, 0),
            (200, -1, 9000), (1, 0, 9000), (int.MaxValue, 0, 9000),
            (0, int.MaxValue, 0), (int.MaxValue, int.MaxValue, 5000)
        };
        foreach (var attackerLevel in new[] { 1, 90, 140, 200 })
        foreach (var targetLevel in new[] { 2, 140, 200, 10_000 })
        foreach (var point in points)
        {
            var attacker = PveRatioAttacker() with
            { Level = attackerLevel, Critical = point.Critical };
            var target = PveRatioTarget() with
            { Level = targetLevel, CriticalResistance = point.Resistance };
            var actual = AuthoredPlayerPveCurrent.CalculateCriticalChanceBasisPoints(attacker, target);
            Check.Equal(point.Chance, actual,
                $"player PvE crit {point.Critical}/{point.Resistance} ignores levels {attackerLevel}/{targetLevel}");
            Check.Equal(AuthoredCombatV2.CalculateCriticalChanceBasisPoints(attacker, target), actual,
                "players contest monster critical resistance using the same PvP policy");
        }
    }

    private static void CheckPlayerPveCriticalRolls()
    {
        var attacker = PveRatioAttacker();
        var target = PveRatioTarget() with { Level = 140 };
        Check.Equal(8412, AuthoredPlayerPveCurrent.CalculateHitChanceBasisPoints(attacker, target),
            "the established level-140 Hit4000/Dodge6000 PvE accuracy remains 84.12 percent");
        var boundary = AuthoredPlayerPveCurrent.ResolveBasicAttack(attacker, target, eventId: 7);
        Check.True(boundary.Rolls.HitRollBasisPoints == 1083 &&
            boundary.Rolls.CriticalRollBasisPoints == 2868 && boundary.IsCritical,
            "the established event7 raw rolls now give 200-vs-100 player PvE a critical hit");
        var incoming = AuthoredCombatPveCurrent.ResolveBasicAttack(attacker, target, eventId: 7);
        Check.Equal(AuthoredCombatV1.ResolveBasicAttack(attacker, target, eventId: 7), incoming,
            "incoming monster attacks remain on their existing versioned policy");
        Check.True(incoming.FormulaVersion == 1 && !incoming.IsCritical && incoming.Hit,
            "this player-only change does not multiply a monster's critical chance");

        var outcomes = new HashSet<CombatHitOutcome>();
        foreach (var directAccuracy in new[] { false, true })
        foreach (var targetOrder in new[] { 0, 3 })
        for (ulong eventId = 1; eventId <= 128; eventId++)
        {
            var defender = target with { UsesDirectRatingAccuracy = directAccuracy };
            var old = AuthoredCombatV1.ResolveBasicAttack(attacker, defender, eventId, targetOrder);
            var actual = AuthoredPlayerPveCurrent.ResolveBasicAttack(attacker, defender, eventId, targetOrder);
            var pvp = AuthoredCombatPvpCurrent.ResolveBasicAttack(attacker, defender, eventId, targetOrder);
            Check.True(actual.FormulaVersion == 5 && actual.EventId == eventId &&
                actual.TargetOrder == targetOrder && actual.Hit == old.Hit &&
                actual.Rolls.HitChanceBasisPoints == old.Rolls.HitChanceBasisPoints &&
                actual.Rolls.HitRollBasisPoints == old.Rolls.HitRollBasisPoints,
                "normal and encounter-specific accuracy retain their exact admission and raw rolls");
            Check.Equal(6666, actual.Rolls.CriticalChanceBasisPoints,
                "only landed attacks contest 200 critical against 100 resistance at 66.66 percent");
            if (!actual.Hit)
            {
                Check.True(actual.Damage == 0 && actual.CapturedDamageValue == uint.MaxValue &&
                    actual.Rolls.CriticalRollBasisPoints == CombatRollEvidence.NotRolled,
                    "a missed attack cannot roll a critical or apply damage");
            }
            else
            {
                Check.Equal(old.Rolls.CriticalRollBasisPoints, actual.Rolls.CriticalRollBasisPoints,
                    "the new critical policy does not reroll an admitted combat event");
                if (pvp.Hit)
                {
                    Check.True(actual.Rolls.CriticalRollBasisPoints == pvp.Rolls.CriticalRollBasisPoints &&
                        actual.Outcome == pvp.Outcome, "landed PvE and PvP share the critical decision");
                }
                var sameOutcome = AuthoredCombatV1.ResolveBasicAttackForOutcome(attacker, defender, actual.Outcome);
                Check.True(actual.Damage == sameOutcome.Damage && actual.Evidence == sameOutcome.Evidence,
                    "a selected normal or critical retains all existing damage and defense arithmetic");
            }
            outcomes.Add(actual.Outcome);
        }
        Check.Equal(3, outcomes.Count, "the replay corpus covers normal, critical, and miss outcomes");
    }

    private static void CheckPlayerPveBasicAdapters()
    {
        var offense = PveRatioOffense();
        var character = PveRatioCharacter();
        var attacker = PveRatioAttacker();
        var target = CreateResolutionTarget() with
        {
            Level = 200, PhysicalDefense = 250, MagicDefense = 250, Dodge = 6000,
            CriticalResistance = 100, PhysicalDamageReductionBasisPoints = 0,
            PhysicalFlatAbsorption = 0
        };
        Check.Equal(attacker, CombatCharacterStatsAdapter.FromCharacter(character),
            "legacy character fixture represents the exact 200-critical mage stats");
        Check.Equal(attacker, CombatCharacterStatsAdapter.FromOffense(offense),
            "ECS offense fixture represents the same character stats");

        foreach (var outcome in new[] { CombatHitOutcome.Normal, CombatHitOutcome.Critical, CombatHitOutcome.Miss })
        {
            ulong revision = 1;
            CombatResolution expected;
            do
            {
                var eventId = CombatEventIdentity.ForPlayerMonsterBasicAttack(7, target.ObjectId,
                    target.SpawnGeneration, target.HealthRevision, revision);
                expected = AuthoredPlayerPveCurrent.ResolveBasicAttack(attacker, ToTargetStats(target), eventId);
                if (expected.Outcome == outcome) break;
                revision++;
            } while (revision <= 1000);
            Check.True(revision <= 1000, $"a deterministic {outcome} basic fixture exists");
            var fixture = CreateFixture();
            fixture.World.Set(fixture.Player, offense);
            fixture.World.Get<PlayerCombatResourceComponent>(fixture.Player).CombatRevision = revision - 1;
            PlayerCombatEcsBoundary.HydrateTarget(fixture.World, target);
            QueueBasic(fixture, target, Start);
            fixture.Scheduler.RunTick(TimeSpan.Zero);
            var actual = Events<PlayerCombatTargetResolvedEvent>(fixture).Single().Resolution;
            Check.Equal(expected, actual, $"ECS admission applies the new {outcome} critical policy");
            Check.Equal(expected, MonsterCombatResolver.ResolvePlayerBasicAttack(
                character, ToTargetStats(target), expected.EventId),
                $"legacy and ECS basic paths agree for {outcome}");
            Check.Equal(outcome == CombatHitOutcome.Miss ? 0 : 1,
                Events<PlayerCombatDamageIntentEvent>(fixture).Length,
                "only a landed attack creates a monster health mutation");
            Check.Equal(outcome switch
            {
                CombatHitOutcome.Normal => 750u,
                CombatHitOutcome.Critical => 1125u,
                _ => 0u
            }, actual.Damage, "basic 1000 attack against250 defense retains normal/critical damage");
            AssertBasicAttackPacketOutcome(actual);
        }
    }

    private static void CheckPlayerPveSkillAdapters()
    {
        var attacker = PveRatioAttacker();
        var target = PveRatioTarget();
        var character = PveRatioCharacter();
        foreach (var skillId in new[] { 0, 500, 574 })
        {
            Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(skillId, out var skill),
                "published physical, Fireball and Flame Blast V definitions exist");
            var snapshot = PlayerCombatEcsRequest.HostileSkill(skill.Range > 0
                ? PlayerCombatIntentKind.AreaSkill : PlayerCombatIntentKind.SingleTargetSkill,
                Start, 1, skill).Skill;
            var outcomes = new HashSet<CombatHitOutcome>();
            for (ulong eventId = 1; eventId <= 128; eventId++)
            {
                var actual = SkillCombatResolver.ResolveDamage(character, skill, target, eventId, targetOrder: 2);
                Check.Equal(actual, PlayerCombatRules.ResolveSkillDamage(attacker, target, snapshot, eventId, targetOrder: 2),
                    "live legacy and ECS skill snapshot paths share the new critical chance");
                Check.Equal(actual, PlayerSkillDamageFormula.Resolve(attacker, target, skill, eventId, targetOrder: 2),
                    "published definition overload uses the same chance and damage");
                Check.True(actual.FormulaVersion == 5 && actual.Rolls.CriticalChanceBasisPoints == 6666,
                    "all damaging skills, including Flame Blast pulses, use the same player PvE critical policy");
                var old = AuthoredCombatV1.ResolveSkillDamage(attacker, target, skill.Property,
                    skill.Power1, skill.Power2, eventId, targetOrder: 2);
                Check.True(actual.Hit == old.Hit && actual.Rolls.HitRollBasisPoints == old.Rolls.HitRollBasisPoints,
                    "skill accuracy and raw rolls remain unchanged");
                var forced = PlayerSkillDamageFormula.ResolveForOutcome(attacker, target, skill, actual.Outcome);
                Check.Equal(forced, PlayerSkillDamageFormula.ResolveForOutcome(attacker, target, snapshot, actual.Outcome),
                    "definition and snapshot forced-outcome overloads preserve complete evidence");
                Check.True(actual.Damage == forced.Damage && actual.Evidence == forced.Evidence,
                    "random resolution carries the correct selected damage outcome");
                if (!actual.Hit)
                    Check.True(actual.CapturedDamageValue == uint.MaxValue && actual.Damage == 0 &&
                        actual.Rolls.CriticalRollBasisPoints == CombatRollEvidence.NotRolled,
                        "a missed Flame Blast or other skill never rolls critical or applies damage");
                if (skillId == 574)
                    Check.Equal(actual.Outcome switch
                    {
                        CombatHitOutcome.Normal => 1215u,
                        CombatHitOutcome.Critical => 1823u,
                        _ => 0u
                    }, actual.Damage, "Flame Blast V retains 750+(750+180)*50 percent before critical scaling");
                outcomes.Add(actual.Outcome);
            }
            Check.Equal(3, outcomes.Count, $"skill{skillId} exercises normal, critical and miss outcomes");

            var fallback = skill with { Target = 0, AffectObj = 0 };
            var fallbackSnapshot = snapshot with { Target = 0, AffectObject = 0 };
            var expectedFallback = AuthoredPlayerPveCurrent.ResolveSkillDamage(attacker, target,
                skill.Property, skill.Power1, skill.Power2, eventId: 7);
            Check.Equal(expectedFallback, PlayerSkillDamageFormula.Resolve(attacker, target, fallback, eventId: 7),
                "legacy-shaped fallback skill also receives the new critical policy");
            Check.Equal(expectedFallback, PlayerSkillDamageFormula.Resolve(attacker, target, fallbackSnapshot, eventId: 7),
                "snapshot fallback skill also receives the new critical policy");
        }
    }

    private static CombatAttackerStats PveRatioAttacker() => new()
    {
        Level = 140, Profession = 3, PhysicalAttack = 1000, MagicAttack = 1000, Hit = 4000, Critical = 200
    };

    private static CombatTargetStats PveRatioTarget() => new()
    {
        Level = 200, PhysicalDefense = 250, MagicDefense = 250, Dodge = 6000, CriticalResistance = 100
    };

    private static PlayerCombatOffenseComponent PveRatioOffense() =>
        new(3, 1000, 1000, 0, 0, 0, 0) { Level = 140, Hit = 4000, Critical = 200 };

    private static GameCharacter PveRatioCharacter() => new()
    {
        Level = 140, Profession = 3,
        CalculatedStats = new CharacterStats
        { PhysicalAttack = 1000, MagicAttack = 1000, Hit = 4000, Critical = 200 }
    };
}
