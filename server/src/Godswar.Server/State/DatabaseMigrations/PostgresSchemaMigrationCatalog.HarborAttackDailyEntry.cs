namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateHarborAttackDailyEntry() => new(
        "20260929_216_harbor_attack_daily_entry",
        "Admit 港湾遇袭 as a third daily-limited legacy instance",
        """
        -- Atlantis is kind 1 and Wonderland is kind 2. Both the settings table
        -- and the daily-entry ledger pinned instance_kind with an inline column
        -- check, so 港湾遇袭 (kind 3) is rejected by the database today. The
        -- checks are data-defined rather than by name: version 132's table was
        -- created inline and its generated constraint name is not part of any
        -- release contract.
        DO $harbor_attack_kind$
        DECLARE
            legacy_check record;
        BEGIN
            FOR legacy_check IN
                SELECT
                    schema_namespace.nspname AS schema_name,
                    relation.relname AS table_name,
                    candidate.conname AS constraint_name
                FROM pg_catalog.pg_constraint AS candidate
                JOIN pg_catalog.pg_class AS relation
                  ON relation.oid = candidate.conrelid
                JOIN pg_catalog.pg_namespace AS schema_namespace
                  ON schema_namespace.oid = relation.relnamespace
                JOIN pg_catalog.pg_attribute AS kind_column
                  ON kind_column.attrelid = candidate.conrelid
                 AND kind_column.attnum = ANY(candidate.conkey)
                WHERE schema_namespace.nspname = 'public'
                  AND relation.relname IN (
                      'legacy_instance_settings',
                      'legacy_instance_daily_entries')
                  AND candidate.contype = 'c'
                  AND kind_column.attname = 'instance_kind'
                  AND NOT kind_column.attisdropped
                  AND strpos(
                      pg_catalog.pg_get_constraintdef(candidate.oid),
                      'instance_kind') > 0
            LOOP
                EXECUTE format(
                    'ALTER TABLE %I.%I DROP CONSTRAINT %I',
                    legacy_check.schema_name,
                    legacy_check.table_name,
                    legacy_check.constraint_name);
            END LOOP;
        END
        $harbor_attack_kind$;

        ALTER TABLE public.legacy_instance_settings
            ADD CONSTRAINT ck_legacy_instance_settings_instance_kind
            CHECK (instance_kind BETWEEN 1 AND 3);

        ALTER TABLE public.legacy_instance_daily_entries
            ADD CONSTRAINT ck_legacy_instance_daily_entries_instance_kind
            CHECK (instance_kind BETWEEN 1 AND 3);

        -- The reference's own page text for the level-50+ entry is a single
        -- daily opportunity, and 港湾遇袭 has no paid retry of any kind, which
        -- is paid_retry_limit = 0 in the store's representation.
        INSERT INTO public.legacy_instance_settings (
            instance_kind,
            free_entry_limit,
            paid_retry_limit)
        VALUES (3, 1, 0);
        """);
}
