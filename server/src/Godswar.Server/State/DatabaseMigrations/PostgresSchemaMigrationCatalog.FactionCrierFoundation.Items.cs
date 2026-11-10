namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string FactionCrierItemSql =
        """
        ALTER TABLE public.item_template_content_revisions
            ALTER COLUMN source TYPE varchar(128);

        WITH nameplates(id, name_key, display_name, icon) AS (
            VALUES
                (3820, 'Nameplate1', 'Nameplate 1', '216,936'),
                (3821, 'Nameplate2', 'Nameplate 2', '252,936'),
                (3822, 'Nameplate3', 'Nameplate 3', '288,936'),
                (3823, 'Nameplate4', 'Nameplate 4', '324,936'),
                (3824, 'Nameplate5', 'Nameplate 5', '360,936'),
                (3825, 'Nameplate6', 'Nameplate 6', '396,936')
        )
        INSERT INTO public.item_templates (
            id, kind, name_key, display_name, equipment_slot, class_ids,
            min_level, max_level, hand, skill_flag, texture, icon, stats
        )
        SELECT
            id,
            'consume item',
            name_key,
            display_name,
            0,
            ARRAY[]::smallint[],
            NULL,
            NULL,
            NULL,
            NULL,
            './Localization/en_us/UI/Texture/Icon.gwo',
            icon,
            jsonb_build_object(
                'ID', id::text,
                'Type', 'consume item',
                'Texture', './Localization/en_us/UI/Texture/Icon.gwo',
                'Icon', icon,
                'Random', '0',
                'Distribution', '150,200',
                'Money', '0',
                'Overlap', '99',
                'BindType', '1'
            )
        FROM nameplates
        ON CONFLICT (id) DO NOTHING;
        """;
}
