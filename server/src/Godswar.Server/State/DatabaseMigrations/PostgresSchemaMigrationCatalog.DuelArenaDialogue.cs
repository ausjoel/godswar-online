namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateDuelArenaDialogueRelease() => new(
        "20260901_139_duel_arena_dialogue_v15",
        "Fence the paired Duel Arena transporter dialogue V15 release",
        """
        DO $duel_arena_dialogue_v15$
        BEGIN
            IF to_regclass('public.npc_dialogue_revisions') IS NULL OR
               to_regclass('public.npc_dialogue_texts') IS NULL OR
               to_regclass('public.npc_dialogue_profiles') IS NULL OR
               to_regclass('public.npc_dialogue_bindings') IS NULL OR
               to_regclass('public.npc_dialogue_profile_entries') IS NULL OR
               to_regclass('public.npc_dialogue_publication') IS NULL THEN
                RAISE EXCEPTION
                    'NPC dialogue publication tables must exist before V15';
            END IF;
        END
        $duel_arena_dialogue_v15$;
        """);
}
