namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateInstanceAttemptDialogueRelease() => new(
        "20260901_134_instance_attempt_dialogue_v13",
        "Fence the three-attempt Instance Caller dialogue V13 release",
        """
        DO $instance_attempt_dialogue_v13$
        BEGIN
            IF to_regclass('public.npc_dialogue_revisions') IS NULL OR
               to_regclass('public.npc_dialogue_texts') IS NULL OR
               to_regclass('public.npc_dialogue_publication') IS NULL THEN
                RAISE EXCEPTION
                    'NPC dialogue publication tables must exist before V13';
            END IF;
        END
        $instance_attempt_dialogue_v13$;
        """);
}
