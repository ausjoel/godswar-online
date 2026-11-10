using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckTransporterDialogueCatalogMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(value =>
            value.Id == "20260901_130_transporter_dialogue_v11");
        Check.Equal(
            "22D25C07427FBFF763B7119402A9906A13B3C65672C97F755531F27AF502B18A",
            migration.Checksum,
            "Transporter dialogue migration checksum is pinned");
        Check.True(
            migration.Sql.Contains(
                "behavior BETWEEN 1 AND 14",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "DROP CONSTRAINT uq_npc_dialogue_profiles_behavior",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ux_npc_dialogue_profiles_non_transporter_behavior",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "WHERE behavior <> 14",
                StringComparison.Ordinal),
            "Transporter migration admits only finite behavior 14 and " +
            "preserves uniqueness for every earlier behavior");
    }
}
