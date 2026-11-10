namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Database-owned monster loot, modelled exactly like the Medusa tables:
    /// one header table per monster plus its indexed drop rules, both editable
    /// without a redeploy. Field and dungeon kills resolve their drops through
    /// <c>MonsterLootContentCatalog</c>, which reads the published rows.
    /// </summary>
    private static PostgresSchemaMigration CreateMonsterLootPolicy() => new(
        "20260915_147_monster_loot_policy",
        "Add the editable monster loot header and drop rules",
        """
        CREATE TABLE IF NOT EXISTS public.monster_loot_tables (
            template_key varchar(64) COLLATE "C" PRIMARY KEY,
            maximum_drops smallint NOT NULL
                CHECK (maximum_drops BETWEEN 1 AND 32),
            enabled boolean NOT NULL DEFAULT true,
            updated_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_monster_loot_tables_template_key
                CHECK (length(btrim(template_key)) > 0)
        );

        CREATE TABLE IF NOT EXISTS public.monster_loot_rules (
            template_key varchar(64) COLLATE "C" NOT NULL,
            loot_index smallint NOT NULL CHECK (loot_index BETWEEN 0 AND 31),
            item_id integer NOT NULL REFERENCES public.item_templates(id),
            chance_basis_points integer NOT NULL
                CHECK (chance_basis_points BETWEEN 1 AND 10000),
            minimum_quantity smallint NOT NULL
                CHECK (minimum_quantity BETWEEN 1 AND 255),
            maximum_quantity smallint NOT NULL
                CHECK (maximum_quantity BETWEEN minimum_quantity AND 255),
            enabled boolean NOT NULL DEFAULT true,
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (template_key, loot_index),
            FOREIGN KEY (template_key)
                REFERENCES public.monster_loot_tables(template_key)
                ON DELETE CASCADE
        );

        -- Capture-proven drops. The item sets come from reference-server
        -- opcode 10029 drop lists (captures/monster-drop-20260915.txt); the
        -- chance is a placeholder until more kills pin the real rate down, and
        -- maximum_drops reproduces the captured "one or two items per kill"
        -- shape.
        INSERT INTO public.monster_loot_tables (template_key, maximum_drops)
        VALUES
            ('A_normal_stub_001', 1),
            ('A_normal_stub_002', 2),
            ('A_normal_deer_001', 1)
        ON CONFLICT (template_key) DO NOTHING;

        INSERT INTO public.monster_loot_rules (
            template_key, loot_index, item_id,
            chance_basis_points, minimum_quantity, maximum_quantity)
        SELECT
            seed.template_key, seed.loot_index, seed.item_id,
            seed.chance_basis_points, seed.minimum_quantity, seed.maximum_quantity
        FROM (VALUES
            -- Captured 2026-09-14 01:08 (Sparta map 0, object 10035).
            ('A_normal_stub_001', 0, 4529, 2500, 1, 1),
            -- Captured 2026-09-14 08:40 and 2026-09-15 01:37 (Athens map 1
            -- and Athens newbie map 2, objects 10471/10476/10477/10492).
            ('A_normal_stub_002', 0, 12030, 2500, 1, 1),
            ('A_normal_stub_002', 1, 4529, 2500, 1, 1),
            ('A_normal_stub_002', 2, 4150, 2500, 1, 1),
            ('A_normal_stub_002', 3, 4003, 2500, 1, 1),
            ('A_normal_stub_002', 4, 4224, 2500, 1, 1),
            ('A_normal_stub_002', 5, 12040, 2500, 1, 1),
            -- Captured 2026-09-14 09:12 (Athens map 1, object 10539).
            ('A_normal_deer_001', 0, 4001, 2500, 1, 1)
        ) AS seed(
            template_key, loot_index, item_id,
            chance_basis_points, minimum_quantity, maximum_quantity)
        -- The complete item catalogue is published only after the schema
        -- migrations have run, so a captured drop can name an item that this
        -- baseline does not carry yet. Skip those rows instead of aborting the
        -- whole migration on the item_templates foreign key.
        WHERE EXISTS (
            SELECT 1
            FROM public.item_templates AS item
            WHERE item.id = seed.item_id)
        ON CONFLICT (template_key, loot_index) DO NOTHING;

        -- MonsterLootContentSnapshot requires every published table to offer at
        -- least as many rules as its drop cap. A captured drop whose item the
        -- baseline does not carry yet is skipped above, so cap each table to the
        -- rules that actually survived and drop tables that kept none.
        UPDATE public.monster_loot_tables AS loot_table
        SET maximum_drops = surviving.rule_count
        FROM (
            SELECT template_key, COUNT(*)::smallint AS rule_count
            FROM public.monster_loot_rules
            GROUP BY template_key) AS surviving
        WHERE surviving.template_key = loot_table.template_key
          AND loot_table.maximum_drops > surviving.rule_count;

        DELETE FROM public.monster_loot_tables AS loot_table
        WHERE NOT EXISTS (
            SELECT 1
            FROM public.monster_loot_rules AS rule
            WHERE rule.template_key = loot_table.template_key);
        """);
}
