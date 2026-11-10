namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateInstanceFreeEntryDialogueRelease() => new(
        "20260901_137_instance_free_entry_dialogue_v14",
        "Fence the three-free-entry Instance Caller dialogue V14 release",
        """
        DO $instance_free_entry_dialogue_v14$
        BEGIN
            IF to_regclass('public.npc_dialogue_revisions') IS NULL OR
               to_regclass('public.npc_dialogue_texts') IS NULL OR
               to_regclass('public.npc_dialogue_publication') IS NULL THEN
                RAISE EXCEPTION
                    'NPC dialogue publication tables must exist before V14';
            END IF;
        END
        $instance_free_entry_dialogue_v14$;
        """);
}
