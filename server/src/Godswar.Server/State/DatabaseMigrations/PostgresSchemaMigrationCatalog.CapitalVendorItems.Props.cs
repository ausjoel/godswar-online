namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string CapitalVendorPropsSql =
        """
        WITH stock(
            id, name_key, display_name, class_ids, min_level, max_level,
            texture, icon, stats
        ) AS (
            VALUES
                (4001, 'HPPotion_b', 'Medium Healing Potion', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '288,972', '{"ID":"4001","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"288,972","Random":"150","Distribution":"50,200","Money":"21","Overlap":"99","Use":"1","Skill":"3101","ItemType":"10"}'::jsonb),
                (4002, 'HPPotion_c', 'Big Healing Potion', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '324,972', '{"ID":"4002","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"324,972","Random":"75","Distribution":"50,200","Money":"39","Overlap":"99","Use":"1","Skill":"3102","ItemType":"10"}'::jsonb),
                (4031, 'MPPotion_b', 'Medium Mana Potion', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '468,972', '{"ID":"4031","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"468,972","Random":"0","Distribution":"50,200","Money":"21","Overlap":"99","Use":"1","Skill":"3121","ItemType":"11"}'::jsonb),
                (4032, 'MPPotion_c', 'Big Mana Potion', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '504,972', '{"ID":"4032","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"504,972","Random":"0","Distribution":"50,200","Money":"39","Overlap":"99","Use":"1","Skill":"3122","ItemType":"11"}'::jsonb),
                (4100, 'GuildStone', 'Guild Stone', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '900,252', '{"ID":"4100","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"900,252","Random":"0","Distribution":"0,0","Money":"0","Overlap":"1","BindType":"1"}'::jsonb),
                (4150, 'Money1', 'Small Money Bag', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '576,252', '{"ID":"4150","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"576,252","Random":"0","Distribution":"0,0","Money":"50000","Overlap":"99","Use":"1","Skill":"4600"}'::jsonb),
                (4151, 'Money2', 'Medium Money Bag', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '612,252', '{"ID":"4151","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"612,252","Random":"0","Distribution":"0,0","Money":"500000","Overlap":"99","Use":"1","Skill":"4601"}'::jsonb),
                (4152, 'Money3', 'Big Money Bag', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '648,252', '{"ID":"4152","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"648,252","Random":"0","Distribution":"0,0","Money":"2500000","Overlap":"99","Use":"1","Skill":"4602"}'::jsonb),
                (3949, 'Earphone3949', 'Firework of Love', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon2.gwo', '540,756', '{"ID":"3949","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"540,756","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","Use":"1","Skill":"4825"}'::jsonb),
                (3939, 'Earphone3939', 'Simple Firework', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '432,792', '{"ID":"3939","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"432,792","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99","Use":"1","Skill":"4821"}'::jsonb),
                (3935, 'Earphone3935', 'Festival Packet (Gold)', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '468,756', '{"ID":"3935","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"468,756","Random":"0","Distribution":"0,0","Money":"0","Overlap":"1","Use":"1","Skill":"5128","SpecialFlag":"BijouBag"}'::jsonb),
                (3936, 'Earphone3936', 'Festival Packet (Silver)', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '468,756', '{"ID":"3936","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"468,756","Random":"0","Distribution":"0,0","Money":"0","Overlap":"1","Use":"1","Skill":"5129","SpecialFlag":"MoneyBag"}'::jsonb),
                (12110, 'Lifing12110', 'Beginner Hoe', ARRAY[0,1,2,3]::smallint[], 1, 200, './Localization/en_us/UI/Texture/Icon2.gwo', '792,396', '{"ID":"12110","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"792,396","Random":"0","Distribution":"0,0","Money":"16","Overlap":"99","Class":"0,1,2,3","PlayLv":"1,200","ItemType":"52"}'::jsonb),
                (12120, 'Lifing12120', 'Beginner Shovel', ARRAY[0,1,2,3]::smallint[], 1, 200, './Localization/en_us/UI/Texture/Icon2.gwo', '504,288', '{"ID":"12120","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"504,288","Random":"0","Distribution":"0,0","Money":"20","Overlap":"99","Class":"0,1,2,3","PlayLv":"1,200","ItemType":"52"}'::jsonb),
                (12130, 'Lifing12130', 'Beginner Arrow', ARRAY[0,1,2,3]::smallint[], 1, 200, './Localization/en_us/UI/Texture/Icon2.gwo', '540,288', '{"ID":"12130","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"540,288","Random":"0","Distribution":"0,0","Money":"23","Overlap":"99","Class":"0,1,2,3","PlayLv":"1,200","ItemType":"52"}'::jsonb),
                (4468, 'box4468', 'Plump Wheat', ARRAY[]::smallint[], NULL, NULL, './Localization/en_us/UI/Texture/Icon.gwo', '612,288', '{"ID":"4468","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon.gwo","Icon":"612,288","Random":"0","Distribution":"0,0","Money":"500","Overlap":"99","Use":"1","Skill":"5568"}'::jsonb)
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
            -1,
            class_ids,
            min_level,
            max_level,
            NULL,
            NULL,
            texture,
            icon,
            stats
        FROM stock
        ON CONFLICT (id) DO NOTHING;
        """;
}
