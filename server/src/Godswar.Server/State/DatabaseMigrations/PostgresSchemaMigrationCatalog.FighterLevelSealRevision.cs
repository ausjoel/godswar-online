namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateFighterLevelSealRevision() =>
        new(
            "20260831_125_fighter_level_seal_revision",
            "Add an authoritative revision for durable level-seal commands",
            """
            ALTER TABLE public.character_base
                ADD COLUMN fighter_level_seal_revision bigint
                    NOT NULL DEFAULT 0,
                ADD CONSTRAINT
                    ck_character_base_fighter_level_seal_revision
                    CHECK (fighter_level_seal_revision >= 0);

            COMMENT ON COLUMN
                public.character_base.fighter_level_seal_revision IS
                'Monotonic revision advanced by committed seal-state transitions.';
            """);
}
