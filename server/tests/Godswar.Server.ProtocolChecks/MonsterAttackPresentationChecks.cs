using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

// server-skills (2026-11-03): open-world ranged monsters present their own
// skill id instead of 2000; type-3 melee bodies get melee reach.
internal static class MonsterAttackPresentationChecks
{
    public const string CheckName =
        "Open-world monster attack presentation table and melee reach";

    public static Task RunAsync()
    {
        var entries = MonsterAttackPresentationTable.Entries;
        Check.Equal(103, entries.Count, "table lists the 103 researched ranged templates");
        Check.Equal(97, entries.Values.Count(entry => entry.SkillId is not null),
            "97 templates present a skill");
        Check.Equal(6, entries.Values.Count(entry => entry.MeleeReach),
            "exactly the six type-3 melee bodies get melee reach");
        Check.True(entries.Values.All(entry => !(entry.MeleeReach && entry.SkillId is not null)),
            "a melee-reach template keeps the 2000 swing");

        Check.True(MonsterAttackPresentationTable.ImpactSkillId("A_normal_mage_016") == 2013,
            "Witch (Parnitha) casts 2013");
        Check.True(MonsterAttackPresentationTable.ImpactSkillId("B_normalB_centaur_008") == 2036,
            "Centaur (Parnitha Port) shoots 2036");
        Check.True(MonsterAttackPresentationTable.ImpactSkillId("C_normal_wolf_006") == 2011,
            "Red/Anuby Wolf uses the 2008 fire claw 2011");
        Check.True(MonsterAttackPresentationTable.ImpactSkillId("c_eliteA_AthensTower_001") == 2015,
            "Turret uses the Wonderland tower projectile 2015 (the admin 2026-11-03)");
        Check.True(MonsterAttackPresentationTable.ImpactSkillId("A_normal_stub_001") == null,
            "unknown templates are not listed");

        Check.Equal(2013u, GameSessionRegistry.ResolveMonsterPresentationImpactSkillId(
                "A_normal_mage_016", 2000),
            "the first 10046 of a Witch hit carries 2013");
        Check.Equal(2000u, GameSessionRegistry.ResolveMonsterPresentationImpactSkillId(
                "A_normal_stub_001", 2000),
            "an unlisted monster still sends 2000");
        Check.Equal(2000u, GameSessionRegistry.ResolveMonsterPresentationImpactSkillId(
                "B_elitecopy_godsguard_002", 2000),
            "a melee-reach monster still sends 2000");
        Check.Equal(2512u, GameSessionRegistry.ResolveMonsterPresentationImpactSkillId(
                "A_normal_mage_016", 2512),
            "a Medusa effect skill id wins over the table");

        // Reach.
        Check.Equal(3.5f, Reach("B_elitecopy_godsguard_002", MonsterAttackDamageKind.Special, 3.5f),
            "Marduk Guard (type 3) swings from its 3.5 m collision range, not 9 m");
        Check.Equal(3f, Reach("A_boss_kingofscorpion_001", MonsterAttackDamageKind.Special, 3f),
            "Darkmist (type 3) swings from 3 m");
        Check.Equal(MonsterAttackRangePolicy.RangedRange,
            Reach("A_elitecopy_centaur_005", MonsterAttackDamageKind.Special, 3.5f),
            "Centauri Heathen (type 3 archer) keeps 9 m");
        Check.Equal(MonsterAttackRangePolicy.RangedRange,
            Reach("c_eliteA_AthensTower_001", MonsterAttackDamageKind.Special, 2.5f),
            "Turret (type 3, cannot move) keeps 9 m");
        Check.Equal(MonsterAttackRangePolicy.RangedRange,
            Reach("A_normal_mage_016", MonsterAttackDamageKind.Magical, 2.5f),
            "Witch (type 2) keeps 9 m");
        Check.Equal(MonsterAttackRangePolicy.RangedRange,
            Reach("A_normal_stub_001", MonsterAttackDamageKind.Special, 2.5f),
            "an unlisted type-3 monster keeps the generic 9 m rule");

        // Parser rejects bad edits.
        Check.Throws<FormatException>(() => MonsterAttackPresentationTable.Parse(
                "template_key,skill_id,reach\nX,2000,ranged\n"),
            "skill 2000 (or anything outside 2001-2999) is refused");
        Check.Throws<FormatException>(() => MonsterAttackPresentationTable.Parse(
                "template_key,skill_id,reach\nX,2013,far\n"),
            "an unknown reach is refused");
        Check.Throws<FormatException>(() => MonsterAttackPresentationTable.Parse(
                "template_key,skill_id,reach\nX,2013,ranged\nX,2014,ranged\n"),
            "a duplicate template key is refused");
        return Task.CompletedTask;
    }

    private static float Reach(string templateKey, MonsterAttackDamageKind kind, float collision)
    {
        var profile = new MonsterCombatProfile(kind, collision, 100, 1, 1, 1, 1, 1, 1, 1, 1, true, false);
        var definition = new CapturedMonsterSpawn(29, "Execratively3", templateKey, templateKey,
            1, 0f, 0f, new byte[108]);
        return MonsterAttackRangePolicy.Resolve(profile, definition);
    }
}
