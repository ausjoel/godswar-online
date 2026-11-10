namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateWonderlandUnlimitedEntries() => new(
        "20260928_215_wonderland_unlimited_entries",
        "Let the Wonderland daily-entry limit be unlimited",
        """
        -- Version 136 pinned 飘渺幻境 to paid_retry_limit = 0, which the store
        -- reads as "three entries a day and no retry", and it documents NULL as
        -- unlimited. The operator removed the cap on 2026-09-28, so the pin is
        -- dropped and the row takes the unlimited representation.
        ALTER TABLE public.legacy_instance_settings
            DROP CONSTRAINT ck_legacy_instance_settings_wonderland_paid;

        UPDATE public.legacy_instance_settings
        SET paid_retry_limit = NULL,
            updated_at = clock_timestamp()
        WHERE instance_kind = 2;
        """);
}
