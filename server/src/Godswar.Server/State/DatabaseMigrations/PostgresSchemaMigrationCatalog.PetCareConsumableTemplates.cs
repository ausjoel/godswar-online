namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // Source: stock zh_cn ItemBaseAttribute.xml, the same client revision the
    // pet-work table and the food-kind table come from.
    //
    // This migration only covers the pet-care consumables that no reviewed
    // baseline publishes yet, so it cannot collide with the immutable
    // item-template publication. The client-catalog baseline already owns
    // 10002, 10022, 10042, 10060, 4060, 4061, and 4062; the remaining rows
    // below are added to PetItemContentBaseline, which is the authoritative
    // publication. This migration exists so the mutable compatibility table
    // (which carries the character_items foreign key) can project them too.
    //
    // Client rows reproduced verbatim:
    //   Pet10001 食草  Food=1 Fill=20  Favor=6
    //   Pet10003 食草  Food=1 Fill=100 Favor=40
    //   Pet10021 食肉  Food=2 Fill=20  Favor=6
    //   Pet10023 食肉  Food=2 Fill=100 Favor=40
    //   Pet10041 料理  Food=3 Fill=20  Favor=6
    //   Pet10043 料理  Food=3 Fill=100 Favor=40
    //   Pet10061 上等佳酿 Food=0 Fill=0 Favor=20
    //   Pet10090 仙宠泉水 ItemType=8 Values=100 (remaining lifetime)
    private static PostgresSchemaMigration
        CreatePetCareConsumableTemplates() => new(
        "20260924_152_pet_care_consumable_templates",
        "Seed the stock pet-care consumables the installed client ships",
        CapitalVendorPetCareSuppliesSql);

    private const string CapitalVendorPetCareSuppliesSql =
        """
        WITH stock(
            id, display_name, icon, random_value, distribution,
            skill, item_type, food, fill, favor, values_text
        ) AS (
            VALUES
                (10001, 'Green Fodder',  '180,936', '15', '50,200', '4721', '7', '1', '20',  '6',  NULL),
                (10003, 'Sweet Grass',   '252,936', '0', '0,0',   '4721', '7', '1', '100', '40', NULL),
                (10021, 'Steak',         '36,936',  '15', '50,200', '4721', '7', '2', '20',  '6',  NULL),
                (10023, 'Roast Meat',    '108,936', '0', '0,0',   '4721', '7', '2', '100', '40', NULL),
                (10041, 'Rye Bread',     '612,972', '15', '50,200', '4721', '7', '3', '20',  '6',  NULL),
                (10043, 'Royal Feast',   '684,972', '0', '0,0',   '4721', '7', '3', '100', '40', NULL),
                (10061, 'Fine Wine',     '540,936', '15', '50,200', '4721', '7', '0', '0',  '20', NULL),
                (10090, 'Pet Spring Water', '576,936', '10', '50,200', NULL, '8', NULL, NULL, NULL, '100')
        )
        INSERT INTO public.item_templates (
            id, kind, name_key, display_name, equipment_slot, class_ids,
            min_level, max_level, hand, skill_flag, texture, icon, stats
        )
        SELECT
            id,
            'consume item',
            'Pet' || id::text,
            display_name,
            -1,
            ARRAY[]::smallint[],
            NULL,
            NULL,
            NULL,
            NULL,
            './Localization/en_us/UI/Texture/Icon2.gwo',
            icon,
            jsonb_strip_nulls(jsonb_build_object(
                'ID', id::text,
                'Type', 'consume item',
                'Texture', './Localization/en_us/UI/Texture/Icon2.gwo',
                'Icon', icon,
                'Random', random_value,
                'Distribution', distribution,
                'Money', '0',
                'Overlap', '99',
                'Use', '1',
                'Skill', skill,
                'ItemType', item_type,
                'Food', food,
                'Fill', fill,
                'Favor', favor,
                'Values', values_text
            ))
        FROM stock
        ON CONFLICT (id) DO NOTHING;

        -- 10000/10020/10040 predate this feature in the mutable table with
        -- client random-drop values (Random 50, Distribution 50,200). The
        -- reviewed baseline declares them as deterministic care items, so
        -- normalize exactly those three rows. Every other column must already
        -- agree, otherwise the row is left untouched and the publication
        -- validator fails closed.
        UPDATE public.item_templates
        SET stats = jsonb_set(
                jsonb_set(stats, '{Random}', '"0"'),
                '{Distribution}', '"0,0"')
        WHERE id IN (10000, 10020, 10040)
          AND kind = 'consume item'
          AND stats ->> 'Random' = '50'
          AND stats ->> 'Distribution' = '50,200';
        """;
}
