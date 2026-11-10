using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class AtlantisMonsterTemplatePolicyChecks
{
    public const string CheckName =
        "Atlantis approved monster identities and terrain placement contracts";

    public static Task RunAsync()
    {
        var expected = new[]
        {
            ("Mudskipper", "Crab Eating Frog", "[Elite]Mudskipper", "Dinna"),
            ("Fiery Turtle", "Deep Sea Spider", "[Elite]Shoal Crocodile",
                "3-headed Sea Serpent"),
            ("Skeletal Swordsman", "Evil Spirit", "[Elite]Evil Spirit",
                "Raging Spirit"),
            ("Defensive Witch", "Bewitching Mage", "[Elite]Bewitching Mage",
                "Prophet"),
            ("Mudskipper", "Evil Spirit", "[Elite]Defensive Witch",
                "Dinna the Sea Guard")
        };
        var totalPoints = 0;
        for (var stage = 0; stage < expected.Length; stage++)
        {
            var group = Enumerable.Range(0, 12).Select(slot =>
                AtlantisMonsterTemplatePolicy.ResolveGroupMember(stage, slot))
                .ToArray();
            Check.Equal(10, group.Count(value =>
                value.Rank == AtlantisMonsterRank.Normal),
                "each group contains ten normal monsters");
            Check.Equal(2, group.Count(value =>
                value.Rank == AtlantisMonsterRank.Elite),
                "each group contains two elite monsters");
            Check.Equal(12, group.Select(value => (value.X, value.Z))
                .Distinct().Count(), "group spawn positions are distinct");
            for (var slot = 0; slot < group.Length; slot++)
            {
                AssertPublishedTemplate(group[slot], slot < 5
                    ? expected[stage].Item1
                    : slot < 10 ? expected[stage].Item2 : expected[stage].Item3);
                for (var other = slot + 1; other < group.Length; other++)
                {
                    var dx = group[slot].X - group[other].X;
                    var dz = group[slot].Z - group[other].Z;
                    Check.True(dx * dx + dz * dz >= 16f,
                        "group spawns leave four units between centers");
                }
            }

            var boss = AtlantisMonsterTemplatePolicy.ResolveBoss(stage);
            Check.True(group.Average(value => value.X) == -74f &&
                group.Average(value => value.Z) == 31f &&
                boss.X == -74f && boss.Z == 31f,
                "every group and boss use the requested (-74,31) center");
            AssertPublishedTemplate(boss, expected[stage].Item4);
            Check.True(boss.Rank == AtlantisMonsterRank.Boss,
                "each stage ends with an authoritative boss template");
            var stagePoints = 4 * group.Sum(Points) + Points(boss);
            Check.Equal(170, stagePoints,
                "four groups plus the boss supply 170 points");
            totalPoints += stagePoints;
        }

        Check.Equal(AtlantisEncounterPolicy.CompletionTeamPoints, totalPoints,
            "the finite approved roster supplies exactly the completion target");
        Check.True(AtlantisMonsterTemplatePolicy.MinimumBlockedCellClearance >
            AtlantisMonsterCombatPolicy.Behavior.MaximumRoamRadius,
            "verified spawn clearance contains the Atlantis idle roam radius");
        foreach (var stage in new[] { -1, 5, int.MaxValue })
        {
            Check.Throws<ArgumentOutOfRangeException>(() =>
                AtlantisMonsterTemplatePolicy.ResolveGroupMember(stage, 0),
                "an unknown stage never substitutes another roster");
            Check.Throws<ArgumentOutOfRangeException>(() =>
                AtlantisMonsterTemplatePolicy.ResolveBoss(stage),
                "an unknown stage never substitutes another boss");
        }
        foreach (var slot in new[] { -1, 12, int.MaxValue })
        {
            Check.Throws<ArgumentOutOfRangeException>(() =>
                AtlantisMonsterTemplatePolicy.ResolveGroupMember(0, slot),
                "an invalid group slot cannot create an extra scoring enemy");
        }
        return Task.CompletedTask;
    }

    private static int Points(AtlantisMonsterSpawnIdentity identity)
    {
        Check.True(AtlantisEncounterPolicy.TryGetKillPoints(
            identity.Rank, out var points), "every authored monster has a score");
        return points;
    }

    private static void AssertPublishedTemplate(
        AtlantisMonsterSpawnIdentity identity,
        string expectedName)
    {
        var seed = MonsterTemplateSeeds.Monsters.Single(value =>
            value.SourceMapId == 205 && value.TemplateKey == identity.TemplateKey);
        Check.Equal(AtlantisMonsterTemplatePolicy.SceneKey, seed.SceneKey,
            "the exact template belongs to the Atlantis client scene");
        Check.Equal(expectedName, seed.DisplayName,
            "the authored stage uses the approved monster species");
        Check.Equal(identity.Rank.ToString().ToLowerInvariant(), seed.Rank,
            "the authored rank matches the published gameplay seed");
        Check.True(!seed.IsPet && seed.IsBoss ==
            (identity.Rank == AtlantisMonsterRank.Boss) && seed.IsElite ==
            (identity.Rank == AtlantisMonsterRank.Elite),
            "rank flags agree with the published template identity");
        Check.True(float.IsFinite(identity.X) && float.IsFinite(identity.Z) &&
            identity.Y == 0f && identity.X is >= -78f and <= -70f &&
            identity.Z is >= 25f and <= 37f,
            "every spawn remains in the verified central-room footprint");
    }
}
