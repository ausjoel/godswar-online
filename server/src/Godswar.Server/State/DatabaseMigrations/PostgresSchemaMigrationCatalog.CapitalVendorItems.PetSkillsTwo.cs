namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string CapitalVendorPetSkillBooksTwoSql =
        """
        WITH stock(id, display_name, icon, pet_skill) AS (
            VALUES
                (10209, 'Pet Skill: Pixie Dust I', '216,972', '805'),
                (10204, 'Pet Skill: Tear I', '216,972', '605'),
                (10207, 'Pet Skill: Immortal Kiss I', '216,972', '705'),
                (10216, 'Pet Skill: Guard I', '216,972', '1205'),
                (10201, 'Pet Skill: Life Totem I', '216,972', '405'),
                (10213, 'Pet Skill: Concentration I', '216,972', '1005'),
                (10205, 'Pet Skill: Feather Blade I', '216,972', '608'),
                (10210, 'Pet Skill: Dark Vengeance I', '216,972', '808'),
                (10225, 'Pet Skill: Extraction I', '216,972', '2005'),
                (10232, 'Pet Skill: Ocean Sphere I', '216,972', '2700'),
                (10295, 'Pet Skill: Tiger''s Roar I', '216,972', '2711'),
                (10298, 'Pet Skill: Iceshot I', '216,972', '2800'),
                (10307, 'Pet Skill: Evasion I', '216,972', '3100'),
                (10410, 'Pet Skill: Frozen Blessing I', '216,972', '454'),
                (10301, 'Pet Skill: Eagle Eye I', '216,972', '2900'),
                (10304, 'Pet Skill: Magic Barrier I', '216,972', '3000'),
                (10416, 'Pet Skill: Sphinx''s Enigma I', '216,972', '1930'),
                (10422, 'Pet Skill:Mind Refresh I', '216,972', '530'),
                (10428, 'Pet Skill:Imp Trick I', '216,972', '3124'),
                (10434, 'Pet Skill:Mean Streak I', '216,972', '3148'),
                (10440, 'Pet Skill:Primal Spirit I', '216,972', '3172'),
                (10446, 'Pet Skill:Prick I', '216,972', '3300'),
                (10452, 'Pet Skill:Penalty of Justice I', '216,972', '3500'),
                (10458, 'Pet Skill:Palm Sweep I', '216,972', '3700'),
                (10470, 'Pet Skill:Fury of Justice I', '216,972', '4100'),
                (10476, 'Pet Skill:Gnarl I', '216,972', '4300'),
                (10600, 'Pet Skill: Discharge I', '216,972', '5400'),
                (10610, 'Pet Skill: Eclipse I', '216,972', '5500'),
                (10710, 'Pet Skill: Magission I', '216,972', '6100'),
                (10720, 'Pet Skill: Sacrifice I', '216,972', '6200'),
                (10730, 'Pet Skill: Lifedrain I', '216,972', '6300'),
                (10740, 'Pet Skill: Spiky Armor I', '216,972', '6000')
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
            jsonb_build_object(
                'ID', id::text,
                'Type', 'consume item',
                'Texture', './Localization/en_us/UI/Texture/Icon2.gwo',
                'Icon', icon,
                'Random', '0',
                'Distribution', '0,0',
                'Money', '0',
                'Overlap', '99',
                'Use', '1',
                'ItemType', '4',
                'PetSkill', pet_skill
            )
        FROM stock
        ON CONFLICT (id) DO NOTHING;
        """;
}
