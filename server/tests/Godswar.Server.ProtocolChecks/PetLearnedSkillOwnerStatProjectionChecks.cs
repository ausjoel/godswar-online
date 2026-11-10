using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetLearnedSkillOwnerStatProjectionChecks
{
    public const string CheckName =
        "Summoned-pet learned-skill owner-stat projection";

    public static Task RunAsync()
    {
        var content = PetLearnedSkillContentBaseline.Create();
        AssertEffect(content, 408, 6, 5.59m, 19, 0.035m);
        AssertEffect(content, 408, 6, 100m, 19, 0.041m);
        AssertEffect(content, 412, 6, 100m, 4, 461m);
        AssertEffect(content, 413, 6, 5.59m, 2, 108m);
        AssertEffect(content, 413, 6, 100m, 2, 119m);
        AssertEffect(content, 419, 6, 100m, 21, 0.080m);
        AssertEffect(content, 423, 6, 100m, 0, 12_500m);
        // The installed client states Dark Vengeance as 40 magic attack that
        // becomes 75 at pet rank 8 and 100 at pet rank 19.
        AssertEffect(content, 62, 1, 0m, 6, 40m);
        AssertEffect(content, 62, 1, 7.99m, 6, 40m);
        AssertEffect(content, 62, 1, 8m, 6, 75m);
        AssertEffect(content, 62, 1, 18.99m, 6, 75m);
        AssertEffect(content, 62, 1, 19m, 6, 100m);
        // Blood Chant is a flat on-hit owner heal, and the Luck Mark boosts
        // the pet itself.
        AssertEffect(content, 340, 1, 6m, 34, 60m);
        AssertEffect(content, 480, 1, 7m, 48, 5m);

        var cte = PostgresCharacterPetLearnedSkillProjectionSql
            .CommonTableExpression;
        var stats = PostgresCharacterRuntimeItemProjectionSql
            .CalculatedStatsForCharacter;
        Check.True(
            cte.Contains("pet.is_summoned", StringComparison.Ordinal) &&
            cte.Contains("pet.activity_state = 'owned'",
                StringComparison.Ordinal) &&
            cte.Contains("skill.is_active", StringComparison.Ordinal) &&
            !cte.Contains("pet.is_carried", StringComparison.Ordinal) &&
            !cte.Contains("pet.contributes_to_character",
                StringComparison.Ordinal),
            "only the one summoned pet is the passive source; Recall removes it and owner Merge neither duplicates nor removes it");
        Check.True(
            cte.Contains("@petLearnedSkillRevision",
                StringComparison.Ordinal) &&
            cte.Contains("candidate.minimum_pet_rank::numeric <= pet.rank",
                StringComparison.Ordinal) &&
            cte.Contains("ORDER BY candidate.minimum_pet_rank DESC",
                StringComparison.Ordinal) &&
            !cte.Contains("curve.family_type IN", StringComparison.Ordinal),
            "SQL uses the pinned revision and authoritative rank for every family");
        Check.True(
            cte.Contains("curve.effect IN (19, 20, 21, 22, 25, 37)",
                StringComparison.Ordinal) &&
            cte.Contains(
                "curve.effect = 34 AND curve.family_type = 428",
                StringComparison.Ordinal) &&
            cte.Contains("WHEN curve.effect = 2 THEN 'hit'",
                StringComparison.Ordinal) &&
            cte.Contains("WHEN curve.effect = 15 THEN 'status_hit'",
                StringComparison.Ordinal) &&
            cte.Contains(
                "WHEN curve.effect = 38 THEN 'damage_rebound_flat'",
                StringComparison.Ordinal) &&
            cte.Contains("WHERE projected.stat_name IS NOT NULL",
                StringComparison.Ordinal) &&
            stats.Contains("pet_learned_skill_stat_values",
                StringComparison.Ordinal),
            "every reviewed effect maps to a real character stat channel and feeds calculated stats");
        Check.Equal(
            32,
            CountOccurrences(cte, "WHEN curve.effect"),
            "the projection covers every owner effect the installed client uses");
        return Task.CompletedTask;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static void AssertEffect(
        Application.Pets.IPetLearnedSkillContentCatalog content,
        int family,
        int priority,
        decimal rank,
        int expectedEffect,
        decimal expectedValue)
    {
        Check.True(
            PetLearnedSkillResolver.TryResolveEffect(
                content,
                family,
                priority,
                rank,
                out var effect) &&
            effect.Effect == expectedEffect &&
            effect.AbsoluteValue == expectedValue,
            $"family {family} tier {priority} resolves its rank-{rank} absolute effect");
    }
}
