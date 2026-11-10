using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckInstanceFreeEntryDialogueMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260901_137_instance_free_entry_dialogue_v14");
        Check.Equal(
            "A63A6DB8326DB05981F87FD76EA9C99F3F70094409D930F646EF513B7CAEBE01",
            migration.Checksum,
            "Instance-free-entry dialogue V14 migration checksum is pinned");
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
            "V14 publication fence proves the immutable dialogue tables exist");
    }
}
