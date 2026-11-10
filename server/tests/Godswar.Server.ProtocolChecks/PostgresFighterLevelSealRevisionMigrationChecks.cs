using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresFighterLevelSealRevisionMigrationChecks
{
    private const string MigrationId =
        "20260831_125_fighter_level_seal_revision";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id == MigrationId);
        Check.True(
            migration.Sql.Contains(
                "ADD COLUMN fighter_level_seal_revision bigint",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CHECK (fighter_level_seal_revision >= 0)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "Monotonic revision advanced by committed seal-state transitions.",
                StringComparison.Ordinal),
            "Level Sealer commands own a nonnegative durable revision");
        Check.True(
            !migration.Sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase) &&
            !migration.Sql.Contains("DELETE ", StringComparison.OrdinalIgnoreCase),
            "the revision migration preserves existing characters");
        return Task.CompletedTask;
    }
}
