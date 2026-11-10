namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateFactionCrierDialogueCapability() => new(
        "20260821_101_faction_crier_dialogue_v6",
        "Allow the finite Faction Crier dialogue behavior",
        """
        ALTER TABLE public.npc_dialogue_profiles
            DROP CONSTRAINT ck_npc_dialogue_profiles_behavior;
        ALTER TABLE public.npc_dialogue_profiles
            ADD CONSTRAINT ck_npc_dialogue_profiles_behavior
            CHECK (behavior BETWEEN 1 AND 8);
        """);
}
