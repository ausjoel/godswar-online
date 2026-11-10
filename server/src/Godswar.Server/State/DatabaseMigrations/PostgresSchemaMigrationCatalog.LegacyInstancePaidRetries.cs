namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateLegacyInstancePaidRetries() => new(
        "20260901_136_legacy_instance_paid_retries",
        "Separate free legacy-instance entries from paid retry limits",
        """
        ALTER TABLE public.legacy_instance_settings
            ADD COLUMN paid_retry_limit smallint;

        ALTER TABLE public.legacy_instance_settings
            DROP CONSTRAINT
                legacy_instance_settings_daily_entry_limit_check,
            DROP CONSTRAINT
                ck_legacy_instance_settings_free_entry_limit,
            DROP CONSTRAINT
                ck_legacy_instance_settings_wonderland_free,
            DROP COLUMN daily_entry_limit;

        -- Version 133 represented Atlantis as one free entry followed by
        -- two paid entries inside a three-entry total. The clarified stock
        -- policy is three free entries followed by one Opal-funded retry.
        -- Reset both stock rows after removing the predecessor constraints
        -- so every valid, locally configured version-133 policy upgrades.
        UPDATE public.legacy_instance_settings
        SET free_entry_limit = 3,
            paid_retry_limit = CASE instance_kind
                WHEN 1 THEN 1
                WHEN 2 THEN 0
            END,
            updated_at = clock_timestamp();

        ALTER TABLE public.legacy_instance_settings
            ADD CONSTRAINT ck_legacy_instance_settings_free_entry_limit
            CHECK (free_entry_limit BETWEEN 0 AND 99),
            ADD CONSTRAINT ck_legacy_instance_settings_paid_retry_limit
            CHECK (
                paid_retry_limit IS NULL OR
                paid_retry_limit BETWEEN 0 AND 99),
            ADD CONSTRAINT ck_legacy_instance_settings_wonderland_paid
            CHECK ((
                instance_kind <> 2 OR
                paid_retry_limit = 0) IS TRUE);

        COMMENT ON COLUMN
            public.legacy_instance_settings.paid_retry_limit IS
            'Paid retries after free_entry_limit; NULL is unlimited.';
        """);
}
