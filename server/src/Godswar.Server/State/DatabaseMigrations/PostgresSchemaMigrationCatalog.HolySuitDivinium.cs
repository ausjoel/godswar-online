namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateHolySuitDivinium() => new(
        "20260916_150_holy_suit_divinium",
        "Permit the eight-tier Holy Suit release while retaining sealed seven-tier manifests",
        """
        ALTER TABLE public.holy_suit_tier_content_definitions
            DROP CONSTRAINT ck_holy_suit_tier_shape,
            ADD CONSTRAINT ck_holy_suit_tier_shape CHECK (
                (suit_type = 0 AND max_level = 0 AND ware_item_id IS NULL) OR
                (suit_type BETWEEN 1 AND 8 AND max_level = 10 AND ware_item_id IS NOT NULL));
        ALTER TABLE public.holy_suit_consumable_content_definitions
            DROP CONSTRAINT ck_holy_suit_consumable_shape,
            ADD CONSTRAINT ck_holy_suit_consumable_shape CHECK (
                (role = 'ware' AND suit_type BETWEEN 1 AND 8 AND experience_capacity = 0 AND stack_cap = 99) OR
                (role = 'holy_box' AND suit_type IS NULL AND experience_capacity > 0 AND stack_cap = 1) OR
                (role = 'experience_prism' AND suit_type IS NULL AND experience_capacity = 100000000 AND stack_cap = 99));
        ALTER TABLE public.holy_suit_upgrade_content_definitions
            DROP CONSTRAINT ck_holy_suit_upgrade_state,
            ADD CONSTRAINT ck_holy_suit_upgrade_state CHECK (
                ((current_suit_type = 0 AND current_level = 0) OR
                 (current_suit_type BETWEEN 1 AND 8 AND current_level BETWEEN 1 AND 10)) AND
                target_suit_type BETWEEN 1 AND 8 AND target_level BETWEEN 1 AND 10);
        ALTER TABLE public.item_template_content_revisions
            DROP CONSTRAINT ck_item_content_holy_suit_counts,
            ADD CONSTRAINT ck_item_content_holy_suit_counts CHECK (
                (manifest_version IN (1, 2, 3, 4) AND holy_suit_tier_count = 0 AND holy_suit_upgrade_count = 0
                 AND holy_suit_consumable_count = 0 AND holy_suit_policy_count = 0) OR
                (manifest_version IN (5, 6, 7, 8, 9) AND holy_suit_tier_count = 8 AND holy_suit_upgrade_count = 70
                 AND holy_suit_consumable_count = 13 AND holy_suit_policy_count = 1) OR
                (manifest_version = 9 AND holy_suit_tier_count = 9 AND holy_suit_upgrade_count = 80
                 AND holy_suit_consumable_count = 14 AND holy_suit_policy_count = 1));
        """);
}
