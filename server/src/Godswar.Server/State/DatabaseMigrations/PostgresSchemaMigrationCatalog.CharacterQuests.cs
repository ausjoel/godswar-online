namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateCharacterQuestTable() => new(
        "20260908_146_character_quests",
        "Persist every accepted quest with its own progress",
        """
        CREATE TABLE IF NOT EXISTS character_quests (
            character_id integer NOT NULL,
            quest_id integer NOT NULL,
            state smallint NOT NULL DEFAULT 0,
            progress integer NOT NULL DEFAULT 0,
            accepted_at timestamp with time zone NOT NULL DEFAULT now()
        );

        DO $character_quests_guards$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'pk_character_quests'
            ) THEN
                ALTER TABLE character_quests
                    ADD CONSTRAINT pk_character_quests
                    PRIMARY KEY (character_id, quest_id);
            END IF;

            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'ck_character_quests_ranges'
            ) THEN
                ALTER TABLE character_quests
                    ADD CONSTRAINT ck_character_quests_ranges
                    CHECK (quest_id >= 0 AND progress >= 0 AND state >= 0);
            END IF;
        END
        $character_quests_guards$;

        -- Carry the single-quest column pair over as rows. The in-progress quest
        -- is inserted first so a quest that somehow appears in both columns keeps
        -- its in-progress state.
        INSERT INTO character_quests (character_id, quest_id, state, progress)
        SELECT id, quest_current_id, 0, 0
        FROM character_base
        WHERE quest_current_id > 0
        ON CONFLICT (character_id, quest_id) DO NOTHING;

        INSERT INTO character_quests (character_id, quest_id, state, progress)
        SELECT base.id, completed.quest_id, 1, 0
        FROM character_base base
        CROSS JOIN LATERAL unnest(base.quest_completed_ids)
            AS completed(quest_id)
        WHERE completed.quest_id > 0
        ON CONFLICT (character_id, quest_id) DO NOTHING;
        """);
}
