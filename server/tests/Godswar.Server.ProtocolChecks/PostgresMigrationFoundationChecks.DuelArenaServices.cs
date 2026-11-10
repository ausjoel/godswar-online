using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckDuelArenaServicesMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static migration => migration.Id ==
                "20260907_141_duel_arena_services");
        Check.Equal(
            "A4DD52559277CF11FBCAAC06E8403E2C64268A72ADB73464E0673EC304D703A5",
            migration.Checksum,
            "captured Arena service migration checksum is pinned");
        Check.True(
            migration.Sql.Contains("CHECK (behavior BETWEEN 1 AND 17)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains("WHERE behavior NOT IN (14, 15, 16, 17);",
                StringComparison.Ordinal),
            "captured Arena services extend the finite behavior domain and sharing exception");
        Check.True(
            migration.Sql.Contains(
                "CREATE UNIQUE INDEX ux_npc_dialogue_profiles_singleton_behavior",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ON public.npc_dialogue_profiles (revision, behavior)",
                StringComparison.Ordinal) &&
            !migration.Sql.Contains("UPDATE ", StringComparison.Ordinal) &&
            !migration.Sql.Contains("DELETE ", StringComparison.Ordinal) &&
            !migration.Sql.Contains("INSERT ", StringComparison.Ordinal),
            "service migration preserves singleton uniqueness and sealed content rows");
    }
}
