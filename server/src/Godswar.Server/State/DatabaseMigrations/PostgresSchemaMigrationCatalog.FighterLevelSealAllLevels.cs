namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateFighterLevelSealAllLevels() =>
        new(
            "20260831_124_fighter_level_seal_all_levels",
            "Allow players to seal any supported fighter level",
            """
            ALTER TABLE public.character_base
                DROP CONSTRAINT ck_character_base_fighter_level_seal;

            ALTER TABLE public.character_base
                ADD CONSTRAINT ck_character_base_fighter_level_seal
                    CHECK (
                        NOT fighter_level_sealed
                        OR fighter_job_lv BETWEEN 1 AND 200
                    ) NOT VALID;

            ALTER TABLE public.character_base
                VALIDATE CONSTRAINT
                    ck_character_base_fighter_level_seal;

            COMMENT ON COLUMN
                public.character_base.fighter_level_sealed IS
                'Durable authoritative opt-in seal at the fighter current level.';
            """);
}
