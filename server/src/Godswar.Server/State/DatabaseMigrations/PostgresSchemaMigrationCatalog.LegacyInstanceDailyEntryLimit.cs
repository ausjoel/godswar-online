namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateLegacyInstanceDailyEntryLimit() => new(
        "20260901_133_legacy_instance_daily_entry_limit",
        "Make Atlantis and Wonderland daily-entry limits database-owned",
        """
        CREATE TABLE public.legacy_instance_settings (
            instance_kind smallint PRIMARY KEY
                CHECK (instance_kind BETWEEN 1 AND 2),
            daily_entry_limit smallint NOT NULL
                CHECK (daily_entry_limit BETWEEN 1 AND 99),
            free_entry_limit smallint NOT NULL,
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CONSTRAINT ck_legacy_instance_settings_free_entry_limit
                CHECK (free_entry_limit BETWEEN 0 AND daily_entry_limit),
            CONSTRAINT ck_legacy_instance_settings_wonderland_free
                CHECK (
                    instance_kind <> 2 OR
                    free_entry_limit = daily_entry_limit)
        );

        INSERT INTO public.legacy_instance_settings (
            instance_kind,
            daily_entry_limit,
            free_entry_limit)
        VALUES
            (1, 3, 1),
            (2, 3, 3);

        ALTER TABLE public.legacy_instance_daily_entries
            ADD COLUMN admitted_at timestamptz;

        -- Version 132 had no pending/admitted distinction. Every row that
        -- predates this lifecycle was already treated as a consumed entry,
        -- so preserve it as an admission during the live upgrade.
        UPDATE public.legacy_instance_daily_entries
        SET admitted_at = claimed_at;

        ALTER TABLE public.legacy_instance_daily_entries
            ADD CONSTRAINT ck_legacy_instance_daily_entry_admission
            CHECK ((admitted_at IS NULL OR admitted_at >= claimed_at)
                IS TRUE);

        ALTER TABLE public.legacy_instance_daily_entries
            DROP CONSTRAINT legacy_instance_daily_entries_pkey;
        ALTER TABLE public.legacy_instance_daily_entries
            ADD CONSTRAINT legacy_instance_daily_entries_pkey PRIMARY KEY (
                realm_id,
                realm_day,
                instance_kind,
                character_id,
                reservation_id);

        CREATE INDEX ix_legacy_instance_daily_entries_pending
            ON public.legacy_instance_daily_entries (
                claimed_at, reservation_id, character_id)
            WHERE admitted_at IS NULL;
        """);
}
