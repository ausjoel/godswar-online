using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckDuelArenaCatalogMigrations()
    {
        var npcRelease = PostgresSchemaMigrationCatalog.All.Single(
            static migration => migration.Id ==
                "20260901_138_duel_arena_npc_content_v2");
        Check.Equal(
            "5122B0F23BBDFE1A25D6A0BF4664606286B2379B0EB66EE09C9593E5D3354181",
            npcRelease.Checksum,
            "Duel Arena NPC V2 migration checksum is pinned");
        Check.True(
            npcRelease.Sql.Contains(
                "CHECK (behavior BETWEEN 1 AND 16)",
                StringComparison.Ordinal) &&
            !npcRelease.Sql.Contains(
                "DROP INDEX",
                StringComparison.OrdinalIgnoreCase) &&
            !npcRelease.Sql.Contains(
                "CREATE UNIQUE INDEX",
                StringComparison.OrdinalIgnoreCase),
            "Arena V2 widens only the finite behavior domain and preserves " +
            "the singleton-profile index");

        var dialogueRelease = PostgresSchemaMigrationCatalog.All.Single(
            static migration => migration.Id ==
                "20260901_139_duel_arena_dialogue_v15");
        Check.Equal(
            "00CEA50E6F6EFFE69B816DC7CAEEA636AEA4A406407D880AF71914F8D26DB8C6",
            dialogueRelease.Checksum,
            "Duel Arena dialogue V15 migration checksum is pinned");
        Check.True(
            dialogueRelease.Sql.Contains(
                "npc_dialogue_profiles",
                StringComparison.Ordinal) &&
            dialogueRelease.Sql.Contains(
                "npc_dialogue_bindings",
                StringComparison.Ordinal) &&
            dialogueRelease.Sql.Contains(
                "npc_dialogue_profile_entries",
                StringComparison.Ordinal),
            "Arena V15 release fences every dialogue publication table");
    }
}
