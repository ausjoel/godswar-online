namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// The quality, grade and added attributes a reward or drop carries.
    /// </summary>
    /// <remarks>
    /// Both tables are GM-owned, so both get the same shape: the six columns the
    /// item instance needs. <c>NULL</c> means "not configured", which keeps the
    /// existing behaviour - the grant writes quality 1, grade 1 and no attributes,
    /// the same plain item every grant produced before this existed.
    /// <para>
    /// The columns mirror <c>character_items</c>, so what the GM configures is
    /// written through to the instance without a translation step.
    /// </para>
    /// </remarks>
    private static PostgresSchemaMigration CreateRewardItemAttributes() =>
        new(
            "20260927_214_reward_item_attributes",
            "Let a quest reward slot and a loot rule carry item attributes",
            """
            ALTER TABLE public.quest_reward_slots
                ADD COLUMN IF NOT EXISTS item_quality smallint NULL,
                ADD COLUMN IF NOT EXISTS item_grade smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute1 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level1 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute2 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level2 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute3 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level3 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute4 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level4 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute5 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level5 smallint NULL;

            COMMENT ON COLUMN public.quest_reward_slots.item_quality IS
                'NULL leaves the granted item at quality 1; the value becomes character_items.item_quality.';

            ALTER TABLE public.monster_loot_rules
                ADD COLUMN IF NOT EXISTS item_quality smallint NULL,
                ADD COLUMN IF NOT EXISTS item_grade smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute1 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level1 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute2 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level2 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute3 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level3 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute4 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level4 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute5 smallint NULL,
                ADD COLUMN IF NOT EXISTS attribute_level5 smallint NULL;

            COMMENT ON COLUMN public.monster_loot_rules.item_quality IS
                'NULL leaves the picked-up item at quality 1; the value becomes character_items.item_quality.';
            """);
}
