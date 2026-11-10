namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateFactionCrierNzstCalendar() => new(
        "20260821_102_faction_crier_nzst_calendar",
        "Publish the Faction Crier UTC+12 realm calendar",
        """
        INSERT INTO public.faction_crier_balance_revisions (
            revision,
            server_utc_offset_minutes,
            minimum_level,
            weekly_reclaim_gold_cost,
            renewal_gold_cost,
            tier_count,
            option_count,
            created_by
        )
        SELECT 1,
               720,
               minimum_level,
               weekly_reclaim_gold_cost,
               renewal_gold_cost,
               tier_count,
               option_count,
               'migration-102'
        FROM public.faction_crier_balance_revisions
        WHERE revision = 0
          AND server_utc_offset_minutes = -480
          AND sealed_at IS NOT NULL;

        INSERT INTO public.faction_crier_balance_tiers (
            balance_revision,
            minimum_level,
            maximum_level,
            base_experience,
            base_talent_points,
            triple_silver_cost,
            all_six_silver_cost
        )
        SELECT 1,
               minimum_level,
               maximum_level,
               base_experience,
               base_talent_points,
               triple_silver_cost,
               all_six_silver_cost
        FROM public.faction_crier_balance_tiers
        WHERE balance_revision = 0
        ORDER BY minimum_level;

        INSERT INTO public.faction_crier_balance_options (
            balance_revision,
            sub_id,
            currency_code,
            cost,
            multiplier,
            reward_kind
        )
        SELECT 1,
               sub_id,
               currency_code,
               cost,
               multiplier,
               reward_kind
        FROM public.faction_crier_balance_options
        WHERE balance_revision = 0
        ORDER BY sub_id;

        DO $publish_faction_crier_nzst_calendar$
        DECLARE
            published_count integer;
        BEGIN
            UPDATE public.faction_crier_balance_settings
            SET revision = 1,
                updated_by = 'migration-102'
            WHERE setting_id = 1
              AND revision = 0;

            GET DIAGNOSTICS published_count = ROW_COUNT;
            IF published_count <> 1 THEN
                RAISE EXCEPTION
                    'Faction Crier NZST calendar publication was not exact.';
            END IF;
        END;
        $publish_faction_crier_nzst_calendar$;
        """);
}
