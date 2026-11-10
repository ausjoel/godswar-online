using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckInstanceAttemptDialogueMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260901_134_instance_attempt_dialogue_v13");
        Check.Equal(
            "E41A8639D7F874FE2D3CA229A05D17D4F6D87D49E75FB610E013079A52091269",
            migration.Checksum,
            "Instance-attempt dialogue V13 migration checksum is pinned");
        Check.True(
            migration.Sql.Contains(
                "npc_dialogue_revisions",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "npc_dialogue_texts",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "npc_dialogue_publication",
                StringComparison.Ordinal),
            "V13 publication fence proves the immutable dialogue tables exist");
    }
}
