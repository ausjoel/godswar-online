namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateDuelArenaInitialActions() => new(
        "20260907_140_duel_arena_initial_actions",
        "Admit captured Arena endpoint profiles, initial actions, and script alias",
        """
        ALTER TABLE public.npc_dialogue_profile_entries
            DROP CONSTRAINT ck_npc_dialogue_profile_entries_sub_id,
            ADD CONSTRAINT ck_npc_dialogue_profile_entries_sub_id
                CHECK (sub_id BETWEEN -1 AND 1000000);

        ALTER TABLE public.npc_dialogue_bindings
            DROP CONSTRAINT ck_npc_dialogue_bindings_script_key,
            ADD CONSTRAINT ck_npc_dialogue_bindings_script_key
                CHECK (
                    (
                        client_script_key = npc_key
                        OR (
                            npc_key = 'Arena_003'
                            AND client_script_key = 'Arena_002'
                        )
                    )
                    AND client_script_key ~ '^[A-Za-z0-9_]+$'
                );

        DROP INDEX public.ux_npc_dialogue_profiles_singleton_behavior;

        CREATE UNIQUE INDEX ux_npc_dialogue_profiles_singleton_behavior
        ON public.npc_dialogue_profiles (revision, behavior)
        WHERE behavior NOT IN (14, 15, 16);
        """);
}
