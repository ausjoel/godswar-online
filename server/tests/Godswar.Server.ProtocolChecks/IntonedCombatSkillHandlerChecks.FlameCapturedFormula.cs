using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static async Task CheckSharedFlameFormulaAsync(PlayerRuntimeMode mode)
    {
        // The fixture's tier1 default profile has15 magic defense. Independent
        // arithmetic for the accepted rank23 skill:
        // A=1000-15=985; core=985+(985+180)*.5+2185=3752.5.
        // Normal=3752.5*1.2+17=4520; critical=4503*1.5+17=6771.5 ->6772.
        const uint normalDamage = 4_520;
        const uint criticalDamage = 6_772;
        uint[] legalDamage = [normalDamage, criticalDamage];
        var clock = NewFlameClock();
        await using var fixture = await CreateFlameFixtureAsync(mode, clock,
            monsterHealth: 1_000_000);
        fixture.Character.CalculatedStats = new CharacterStats
        {
            MagicAttack = 1_000,
            MagicDamageBonus = 2_000,
            MagicAppendDamage = 17,
            Critical = int.MaxValue,
            Hit = 1_000_000,
            LifeAbsorptionFlat = FlameHealing
        };
        lock (fixture.Character.ZodiacSync)
        {
            fixture.Character.ZodiacSkillGridLevels[0] = 23;
            fixture.Character.ZodiacSkillGridSkillIds[0] = 10_057;
        }
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character,
            advanceWorldRevision: false);
        Check.True(FlameMonsterSnapshots(fixture).All(monster =>
                GameplayContentTestFixtures.Runtime.MonsterCombatProfiles
                    .Resolve(monster.Definition).MagicDefense == 15),
            "shared-formula targets have the independently specified15 magic defense");
        Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(574, out var authored),
            "shared Flame fixture uses the published rankV definition");
        var projected = ZodiacOffensiveSkillProjection.Resolve(fixture.Character, authored);
        Check.True(projected.Applied && projected.Skill.ZodiacFlatRank == 23 &&
            projected.Skill.ZodiacFlatPower == 2_185m &&
            projected.Skill.AuthoredPower2 == 180m && projected.Skill.Mp == 246,
            "real damaging-skill Zodiac projection retains raw180, its2185 contribution and rank23 separately");

        var initialHealth = FlameMonsterHealth(fixture);
        var hits = await CastAndReadInitialFlameAsync(fixture, 3,
            expectedManaCost: projected.Skill.Mp, expectedDamageValues: legalDamage,
            expectedStyle: StyleForDamage);
        var initialAfter = FlameMonsterHealth(fixture);
        var normalHits = ChangedFlameTargets(initialHealth, initialAfter)
            .Count(index => initialHealth[index] - initialAfter[index] == normalDamage);
        Check.True(hits > 0,
            $"{mode}: real native574 admission commits shared-formula damage before recurring pulses");
        await WaitForFlameTimersAsync(fixture, clock, 1);
        for (var ordinal = 1; ordinal <= 9; ordinal++)
        {
            await PrimeFlamePulseStylesAsync(fixture, projected.Skill, ordinal);
            var before = FlameMonsterHealth(fixture);
            hits += (await AdvanceAndReadFlameAsync(fixture, clock,
                TimeSpan.FromSeconds(1), expectedStyle: StyleForDamage)).Targets;
            var after = FlameMonsterHealth(fixture);
            Check.True(ChangedFlameTargets(before, after)
                    .Select(index => before[index] - after[index]).Order().SequenceEqual(legalDamage.Order()),
                "every recurring pulse exercises one normal and one critical target without probabilistic test coverage");
            foreach (var index in ChangedFlameTargets(before, after))
            {
                var applied = before[index] - after[index];
                Check.True(legalDamage.Contains(applied),
                    $"{mode}: pulse{ordinal} preserves rank23, defense, append and the normal/critical formulas");
                if (applied == normalDamage) normalHits++;
            }
        }
        await WaitForFlameRetirementAsync(fixture, clock);

        Check.True(hits >= 5 && normalHits > 0 && FlameMonsterHealth(fixture).All(hp => hp > 500_000),
            "large living targets exercise all ten damage passes without early kill or corpse side effects");
        Check.Equal(50 + hits * FlameHealing, fixture.Character.CurrentHp,
            "each shared-formula target mutation still gives one actual lifesteal contribution");
        Check.Equal(InitialMana - 246, fixture.Character.CurrentMp,
            "all recurring shared-formula pulses retain one projected initial MP charge");
        Check.True(FlameFieldCount(fixture.Handler) == 0 && fixture.Socket.Available == 0,
            "shared-formula field retires after nine continuations with no duplicate native frames");

        static byte StyleForDamage(uint damage) => damage switch
        {
            normalDamage => 1,
            criticalDamage => 0,
            _ => throw new InvalidOperationException("Unexpected Flame damage in style fixture.")
        };
    }
}
