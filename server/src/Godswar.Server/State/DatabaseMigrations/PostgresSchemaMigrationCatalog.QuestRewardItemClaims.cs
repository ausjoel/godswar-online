namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// The quest reward items a character has actually been paid.
    /// </summary>
    /// <remarks>
    /// The client grants the item the answer's reward slot named and then
    /// announces it on opcode 10056; until this server records that claim the
    /// item exists only in the client's own bag and a relog overwrites it, which
    /// is the bug this table closes. One row per (character, quest, slot) makes
    /// the grant idempotent, so a relog's bag synchronisation or a duplicate
    /// announcement can never pay the same slot twice.
    /// </remarks>
    private static PostgresSchemaMigration CreateQuestRewardItemClaims() =>
        new(
            "20260927_211_quest_reward_item_claims",
            "Persist the quest reward items a character was paid",
            """
            CREATE TABLE public.quest_reward_item_claims (
                character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                quest_id integer NOT NULL CHECK (quest_id > 0),
                slot_index smallint NOT NULL CHECK (slot_index BETWEEN 0 AND 7),
                item_id integer NOT NULL
                    REFERENCES public.item_templates(id),
                quantity smallint NOT NULL CHECK (quantity > 0),
                inventory_revision bigint NOT NULL,
                claimed_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (character_id, quest_id, slot_index)
            );

            COMMENT ON TABLE public.quest_reward_item_claims IS
                'Quest reward items the server wrote into the character inventory: the primary key makes each reward slot payable exactly once, and inventory_revision ties the row to the inventory mutation it produced.';
            """);
}
