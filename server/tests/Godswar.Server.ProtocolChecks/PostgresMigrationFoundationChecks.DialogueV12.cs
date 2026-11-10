using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckBattlefieldInstanceDialogueMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260901_131_battlefield_instance_dialogue_v12");
        Check.Equal(
            "AC80A22F58F2C7FD37850A8C7408413AB94D0B2E22F2206B035E4184D20BDB82",
            migration.Checksum,
            "Battlefield and Instance dialogue migration checksum is pinned");
        Check.True(
            migration.Sql.Contains(
                "behavior BETWEEN 1 AND 15",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "DROP INDEX public." +
                "ux_npc_dialogue_profiles_non_transporter_behavior",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ux_npc_dialogue_profiles_singleton_behavior",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "WHERE behavior NOT IN (14, 15)",
                StringComparison.Ordinal),
            "V12 admits finite behavior 15 and keeps earlier behaviors unique");
    }
}
