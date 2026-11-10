namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateGlobalExperienceMultiplier() => new(
        "20260901_129_global_experience_multiplier",
        "Add the bounded global monster-kill experience multiplier",
        """
        ALTER TABLE public.monster_reward_settings
            ADD COLUMN global_experience_multiplier_basis_points integer
                NOT NULL DEFAULT 10000
                CHECK (
                    global_experience_multiplier_basis_points
                        BETWEEN 10000 AND 50000
                );

        COMMENT ON COLUMN public.monster_reward_settings.global_experience_multiplier_basis_points IS
            'Separate 1x-5x multiplier applied after each channel additive monster-kill EXP stack.';
        """);
}
