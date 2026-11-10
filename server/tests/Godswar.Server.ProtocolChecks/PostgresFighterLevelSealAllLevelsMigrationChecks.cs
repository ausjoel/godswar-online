using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresFighterLevelSealAllLevelsMigrationChecks
{
    private const string MigrationId =
        "20260831_124_fighter_level_seal_all_levels";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id == MigrationId);
        var sql = migration.Sql;
        Check.True(
            sql.Contains(
                "DROP CONSTRAINT ck_character_base_fighter_level_seal",
                StringComparison.Ordinal) &&
            sql.Contains(
                "OR fighter_job_lv BETWEEN 1 AND 200",
                StringComparison.Ordinal) &&
            sql.Contains("NOT VALID", StringComparison.Ordinal) &&
            sql.Contains("VALIDATE CONSTRAINT", StringComparison.Ordinal),
            "the forward migration replaces the level-89 implication with the supported fighter range");
        Check.True(
            !sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE ", StringComparison.OrdinalIgnoreCase),
            "the all-level migration preserves every player's current seal choice");

        foreach (var level in Enumerable.Range(1, 200))
        {
            Check.True(
                SealInvariant(sealedLevel: true, level),
                $"sealed level {level} is accepted");
        }
        Check.True(
            SealInvariant(sealedLevel: false, 0) &&
            !SealInvariant(sealedLevel: true, 0) &&
            !SealInvariant(sealedLevel: true, 201),
            "only sealed states remain bounded by the supported fighter range");
        return Task.CompletedTask;
    }

    private static bool SealInvariant(bool sealedLevel, int fighterLevel) =>
        !sealedLevel || fighterLevel is >= 1 and <= 200;
}
