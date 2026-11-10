namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // Source: stock en_us ItemBaseAttribute.xml and EquipName.dat.
    // ItemBaseAttribute SHA256:
    // 6D2BA8D3FF21A6783AB6D0E21B50963A7DB214690F7B759B9EC495808B990701
    // EquipName SHA256:
    // C90A90679EB4DF4633E0388BB4CBC318C30B0705505EE75344191DCFD4FCCF9E
    private const string CapitalVendorPetSuppliesSql =
        """
        -- Source ItemBaseAttribute.xml SHA256:
        -- 6D2BA8D3FF21A6783AB6D0E21B50963A7DB214690F7B759B9EC495808B990701
        -- Source EquipName.dat SHA256:
        -- C90A90679EB4DF4633E0388BB4CBC318C30B0705505EE75344191DCFD4FCCF9E
        WITH stock(
            id, display_name, icon, random_value, distribution,
            skill, item_type, food, fill, favor
        ) AS (
            VALUES
                (10000, 'Fresh Grass', '144,936', '50', '50,200', '4721', '7', '1', '10', '3'),
                (10020, 'Meat Strip', '0,936', '50', '50,200', '4721', '7', '2', '10', '3'),
                (10021, 'Steak', '36,936', '15', '50,200', '4721', '7', '2', '20', '6'),
                (10040, 'Lollypop', '288,936', '50', '50,200', '4721', '7', '3', '10', '3'),
                (10041, 'Rye Bread', '612,972', '15', '50,200', '4721', '7', '3', '20', '6'),
                (10080, 'Wooden Tool', '360,972', '0', '0,0', '4730', '0', NULL, NULL, NULL),
                (10081, 'Iron Tool', '288,972', '0', '0,0', '4731', '0', NULL, NULL, NULL),
                (10082, 'Silver Tool', '396,972', '0', '0,0', '4732', '0', NULL, NULL, NULL),
                (10061, 'Fine Wine', '540,936', '15', '50,200', '4721', '7', '0', '0', '20')
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
                'Favor', favor
            ))
        FROM stock
        ON CONFLICT (id) DO NOTHING;
        """;
}
