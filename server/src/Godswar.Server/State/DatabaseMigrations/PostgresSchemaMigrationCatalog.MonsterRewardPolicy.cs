namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    internal static PostgresSchemaMigration
        CreateMonsterRewardPolicy() => new(
        "20260831_127_monster_reward_policy",
        "Create the mutable monster-reward lower-level gap authority",
        """
        CREATE TABLE public.monster_reward_settings (
            setting_id smallint PRIMARY KEY DEFAULT 1
                CHECK (setting_id = 1),
            maximum_lower_level_gap smallint NOT NULL
                CHECK (maximum_lower_level_gap BETWEEN 0 AND 199),
            revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
            updated_at timestamptz NOT NULL DEFAULT now(),
            updated_by text NOT NULL
                CHECK (length(btrim(updated_by)) BETWEEN 1 AND 128)
        );

        INSERT INTO public.monster_reward_settings (
            setting_id,
            maximum_lower_level_gap,
            revision,
            updated_by
        )
        VALUES (1, 20, 0, 'migration-127');

        COMMENT ON TABLE public.monster_reward_settings IS
            'Mutable singleton authority for monster-kill reward eligibility.';
        COMMENT ON COLUMN
            public.monster_reward_settings.maximum_lower_level_gap IS
            'Largest inclusive player-level lead that still awards monster-kill progression.';

        CREATE FUNCTION public.stamp_monster_reward_settings_update()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $stamp_monster_reward_settings_update$
        BEGIN
            NEW.revision := OLD.revision + 1;
            NEW.updated_at := now();
            RETURN NEW;
        END;
        $stamp_monster_reward_settings_update$;

        CREATE TRIGGER trg_monster_reward_settings_update
        BEFORE UPDATE ON public.monster_reward_settings
        FOR EACH ROW
        EXECUTE FUNCTION public.stamp_monster_reward_settings_update();
        """);
}
