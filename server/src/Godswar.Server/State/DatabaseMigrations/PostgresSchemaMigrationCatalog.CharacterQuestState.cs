namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateCharacterQuestState() => new(
        "20260908_145_character_quest_state",
        "Persist the character's newbie quest progress",
        """
        ALTER TABLE character_base
            ADD COLUMN IF NOT EXISTS quest_current_id integer
                NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS quest_completed_ids integer[]
                NOT NULL DEFAULT ARRAY[]::integer[];

        DO $character_quest_guards$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'ck_character_quest_current_nonnegative'
            ) THEN
                ALTER TABLE character_base
                    ADD CONSTRAINT ck_character_quest_current_nonnegative
                    CHECK (quest_current_id >= 0);
            END IF;
        END
        $character_quest_guards$;
        """);
}
