namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateDuelArenaNpcContentRelease() => new(
        "20260901_138_duel_arena_npc_content_v2",
        "Fence Arena NPC V2 and extend the finite dialogue behavior domain",
        """
        DO $duel_arena_npc_content_v2$
        BEGIN
            IF to_regclass('public.npc_content_revisions') IS NULL OR
               to_regclass('public.npc_spawn_definitions') IS NULL OR
               to_regclass('public.npc_content_publication') IS NULL OR
               to_regclass('public.npc_dialogue_profiles') IS NULL THEN
                RAISE EXCEPTION
                    'NPC content and dialogue tables must exist before Duel Arena V2';
            END IF;
        END
        $duel_arena_npc_content_v2$;

        ALTER TABLE public.npc_dialogue_profiles
            DROP CONSTRAINT ck_npc_dialogue_profiles_behavior;
        ALTER TABLE public.npc_dialogue_profiles
            ADD CONSTRAINT ck_npc_dialogue_profiles_behavior
            CHECK (behavior BETWEEN 1 AND 16);
        """);
}
