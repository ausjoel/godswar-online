using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckDuelArenaInitialActionsMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static migration => migration.Id ==
                "20260907_140_duel_arena_initial_actions");
        Check.Equal(
            "45D60E112BB8C88D0491BE63A34BCE323829E5CD4767317F89DCEDDEF255DC65",
            migration.Checksum,
            "captured Arena initial-action migration checksum is pinned");
        Check.True(
            migration.Sql.Contains(
                "CHECK (sub_id BETWEEN -1 AND 1000000)",
                StringComparison.Ordinal),
            "captured initial actions admit only one additional negative sub-ID");
        Check.True(
            migration.Sql.Contains("client_script_key = npc_key",
                StringComparison.Ordinal) &&
            migration.Sql.Contains("npc_key = 'Arena_003'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains("AND client_script_key = 'Arena_002'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "AND client_script_key ~ '^[A-Za-z0-9_]+$'",
                StringComparison.Ordinal),
            "captured Gatekeeper alias preserves exact-key and ASCII guards");
        Check.True(
            migration.Sql.Contains(
                "DROP INDEX public.ux_npc_dialogue_profiles_singleton_behavior;",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CREATE UNIQUE INDEX ux_npc_dialogue_profiles_singleton_behavior",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ON public.npc_dialogue_profiles (revision, behavior)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains("WHERE behavior NOT IN (14, 15, 16);",
                StringComparison.Ordinal),
            "captured Arena joins only the existing multi-profile travel behaviors");
        Check.True(
            migration.Sql.Split("ALTER TABLE", StringSplitOptions.None)
                .Length == 3 &&
            migration.Sql.Split("DROP CONSTRAINT", StringSplitOptions.None)
                .Length == 3 &&
            migration.Sql.Split("ADD CONSTRAINT", StringSplitOptions.None)
                .Length == 3 &&
            migration.Sql.Split("DROP INDEX", StringSplitOptions.None)
                .Length == 2 &&
            migration.Sql.Split("CREATE UNIQUE INDEX", StringSplitOptions.None)
                .Length == 2 &&
            !migration.Sql.Contains("UPDATE ", StringComparison.Ordinal) &&
            !migration.Sql.Contains("DELETE ", StringComparison.Ordinal) &&
            !migration.Sql.Contains("INSERT ", StringComparison.Ordinal),
            "Arena migration changes two constraint domains and one profile index");
    }
}
