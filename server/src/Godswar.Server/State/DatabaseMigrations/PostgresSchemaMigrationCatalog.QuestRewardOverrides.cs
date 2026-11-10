namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// The GM tool's quest reward overrides: what a quest pays in items, and what
    /// it pays in experience, talent points, silver and gold.
    /// </summary>
    /// <remarks>
    /// Two tables, because they are two different promises. A quest with rows in
    /// <c>quest_reward_slots</c> has its whole reward menu replaced by them - the
    /// slots that are absent are free - while a quest with no row there keeps the
    /// captured menu it shipped with. A row in <c>quest_reward_values</c> replaces
    /// that quest's four currency values the same way.
    /// <para>
    /// Both are read once at startup, exactly like the monster loot tables, so an
    /// edit takes effect on the next server start and never mid-flight.
    /// </para>
    /// </remarks>
    private static PostgresSchemaMigration CreateQuestRewardOverrides() =>
        new(
            "20260927_212_quest_reward_overrides",
            "Own quest reward items and values in the database",
            """
            CREATE TABLE public.quest_reward_slots (
                quest_id integer NOT NULL CHECK (quest_id > 0),
                slot_index smallint NOT NULL CHECK (slot_index BETWEEN 0 AND 7),
                item_id integer NOT NULL
                    REFERENCES public.item_templates(id),
                PRIMARY KEY (quest_id, slot_index)
            );

            COMMENT ON TABLE public.quest_reward_slots IS
                'GM-owned quest reward menu: any row for a quest replaces all of that quest''s reward slots, and the slots left out are free.';

            CREATE TABLE public.quest_reward_values (
                quest_id integer PRIMARY KEY CHECK (quest_id > 0),
                experience integer NOT NULL CHECK (experience >= 0),
                talent_points integer NOT NULL CHECK (talent_points >= 0),
                silver integer NOT NULL CHECK (silver >= 0),
                gold integer NOT NULL CHECK (gold >= 0)
            );

            COMMENT ON TABLE public.quest_reward_values IS
                'GM-owned quest payout: replaces the experience, talent points, silver and gold a quest hand-in pays.';
            """);
}
