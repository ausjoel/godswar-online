namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateDuelArenaServices() => new(
        "20260907_141_duel_arena_services",
        "Admit the three captured Duel Arena service profiles",
        """
        ALTER TABLE public.npc_dialogue_profiles
            DROP CONSTRAINT ck_npc_dialogue_profiles_behavior,
            ADD CONSTRAINT ck_npc_dialogue_profiles_behavior
                CHECK (behavior BETWEEN 1 AND 17);

        DROP INDEX public.ux_npc_dialogue_profiles_singleton_behavior;

        CREATE UNIQUE INDEX ux_npc_dialogue_profiles_singleton_behavior
        ON public.npc_dialogue_profiles (revision, behavior)
        WHERE behavior NOT IN (14, 15, 16, 17);
        """);
}
