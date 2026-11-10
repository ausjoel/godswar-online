namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateBloodfangPetSpecies() => new(
        "20260916_155_bloodfang_pet_species",
        "Add the Bloodfang pet identity and its explicit Magic Jade mapping",
        """
        INSERT INTO public.pet_templates (
            species_id, display_name,
            female_model, female_texture, female_icon,
            male_model, male_texture, male_icon)
        VALUES (
            46, 'Bloodfang',
            'Bloodfang_male_001.jcs', 'Bloodfang_male_001.gwo', '864,900',
            'Bloodfang_male_001.jcs', 'Bloodfang_male_001.gwo', '864,900')
        ON CONFLICT (species_id) DO NOTHING;

        DO $bloodfang_identity$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM public.pet_templates
                WHERE species_id = 46 AND display_name = 'Bloodfang'
                  AND female_model = 'Bloodfang_male_001.jcs'
                  AND female_texture = 'Bloodfang_male_001.gwo'
                  AND female_icon = '864,900'
                  AND male_model = 'Bloodfang_male_001.jcs'
                  AND male_texture = 'Bloodfang_male_001.gwo'
                  AND male_icon = '864,900'
                  AND samsara_appearance_thresholds = ARRAY[0,8,20]::smallint[]
                  AND source_path = 'Localization/en_us/Settings/Sys/Pet.xml'
            ) THEN
                RAISE EXCEPTION 'Bloodfang species 46 conflicts with an existing identity';
            END IF;
        END
        $bloodfang_identity$;

        -- Keep every historical mapping valid. 11095 is Ambrosia of Rebirth,
        -- so the authored species must use the explicitly reserved 11096.
        ALTER TABLE public.pet_content_species_definitions
            DROP CONSTRAINT ck_pet_content_species_magic_jade_range;
        ALTER TABLE public.pet_content_species_definitions
            ADD CONSTRAINT ck_pet_content_species_magic_jade_range
            CHECK (
                (species_id BETWEEN 1 AND 45
                    AND magic_jade_item_id = 11049 + species_id)
                OR (species_id = 46 AND magic_jade_item_id = 11096)
            );
        """);
}
