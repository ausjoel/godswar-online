namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string CapitalVendorPetSkillBooksOneSql =
        """
        WITH stock(id, display_name, icon, pet_skill) AS (
            VALUES
                (10200, 'Pet Skill: Vital Boost I', '144,972', '400'),
                (10202, 'Pet Skill: Meditate I', '144,972', '500'),
                (10203, 'Pet Skill: Sharp Claw I', '144,972', '600'),
                (10206, 'Pet Skill: Holy Shield I', '144,972', '700'),
                (10208, 'Pet Skill: Mystic Oracle I', '144,972', '800'),
                (10211, 'Pet Skill: Sparkling Fog I', '144,972', '900'),
                (10212, 'Pet Skill: Death Spike I', '144,972', '1000'),
                (10214, 'Pet Skill: Brace I', '144,972', '1100'),
                (10215, 'Pet Skill: Force Shield I', '144,972', '1200'),
                (10217, 'Pet Skill: Power Surge I', '144,972', '1300'),
                (10218, 'Pet Skill: Resistance  I', '144,972', '1400'),
                (10219, 'Pet Skill: Solidify I', '144,972', '1500'),
                (10220, 'Pet Skill: Mentality I', '144,972', '1600'),
                (10221, 'Pet Skill: Wind Ward I', '144,972', '1700'),
                (10222, 'Pet Skill: Light Ward I', '144,972', '1800'),
                (10223, 'Pet Skill: Heart Ward I', '144,972', '1900'),
                (10224, 'Pet Skill: Blood Chant I', '144,972', '2000'),
                (10226, 'Pet Skill: Agility I', '180,972', '2100'),
                (10227, 'Pet Skill: Strength I', '180,972', '2200'),
                (10228, 'Pet Skill: Accuracy I', '180,972', '2300'),
                (10229, 'Pet Skill: Technique I', '180,972', '2400'),
                (10230, 'Pet Skill: Wisdom I', '180,972', '2500'),
                (10231, 'Pet Skill: Luck I', '180,972', '2600'),
                (10486, 'Pet Skill: Spirit Strength I', '216,972', '4400'),
                (10520, 'Pet Skill:  Mesmerise  I', '216,972', '4700'),
                (10540, 'Pet Skill: Ward  I', '216,972', '5100'),
                (10550, 'Pet Skill: Bullseye I', '216,972', '4800'),
                (10560, 'Pet Skill: Scurry I', '216,972', '4900'),
                (10570, 'Pet Skill: Magic Strength I', '216,972', '5300'),
                (10580, 'Pet Skill: Block I', '216,972', '5000')
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
