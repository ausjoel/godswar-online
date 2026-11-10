namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateBattlefieldInstanceDialogueCapability() => new(
        "20260901_131_battlefield_instance_dialogue_v12",
        "Allow finite Battlefield Transporter profiles and expanded instances",
        """
        ALTER TABLE public.npc_dialogue_profiles
            DROP CONSTRAINT ck_npc_dialogue_profiles_behavior;
        ALTER TABLE public.npc_dialogue_profiles
            ADD CONSTRAINT ck_npc_dialogue_profiles_behavior
            CHECK (behavior BETWEEN 1 AND 15);

        DROP INDEX public.ux_npc_dialogue_profiles_non_transporter_behavior;

        CREATE UNIQUE INDEX ux_npc_dialogue_profiles_singleton_behavior
        ON public.npc_dialogue_profiles (revision, behavior)
        WHERE behavior NOT IN (14, 15);
        """);
}
