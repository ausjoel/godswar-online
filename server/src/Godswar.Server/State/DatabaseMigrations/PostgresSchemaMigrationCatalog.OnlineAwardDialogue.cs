namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    internal static PostgresSchemaMigration
        CreateOnlineAwardDialogueCapability() => new(
        "20260821_106_online_award_dialogue_v7",
        "Allow the finite Online Award dialogue behavior",
        """
        ALTER TABLE public.npc_dialogue_profiles
            DROP CONSTRAINT ck_npc_dialogue_profiles_behavior;
        ALTER TABLE public.npc_dialogue_profiles
            ADD CONSTRAINT ck_npc_dialogue_profiles_behavior
            CHECK (behavior BETWEEN 1 AND 9);
        """);
}
