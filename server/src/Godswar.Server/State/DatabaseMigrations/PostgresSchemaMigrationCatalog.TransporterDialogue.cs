namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateTransporterDialogueCapability() => new(
        "20260901_130_transporter_dialogue_v11",
        "Allow endpoint-specific finite Transporter dialogue profiles",
        """
        ALTER TABLE public.npc_dialogue_profiles
            DROP CONSTRAINT ck_npc_dialogue_profiles_behavior;
        ALTER TABLE public.npc_dialogue_profiles
            ADD CONSTRAINT ck_npc_dialogue_profiles_behavior
            CHECK (behavior BETWEEN 1 AND 14);

        ALTER TABLE public.npc_dialogue_profiles
            DROP CONSTRAINT uq_npc_dialogue_profiles_behavior;

        CREATE UNIQUE INDEX
            ux_npc_dialogue_profiles_non_transporter_behavior
        ON public.npc_dialogue_profiles (revision, behavior)
        WHERE behavior <> 14;
        """);
}
