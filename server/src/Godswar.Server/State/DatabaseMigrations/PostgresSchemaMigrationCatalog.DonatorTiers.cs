namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateDonatorTiers() => new(
        "20260831_128_donator_tiers",
        "Rename VIP membership to the five-level Donator tier contract",
        """
        DO $donator_tier_cleanup$
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
                JOIN pg_catalog.pg_attribute AS tier_column
                  ON tier_column.attrelid = candidate.conrelid
                 AND tier_column.attnum = ANY(candidate.conkey)
                WHERE schema_namespace.nspname = 'public'
                  AND relation.relname = 'accounts'
                  AND candidate.contype = 'c'
                  AND tier_column.attname = 'vip_tier'
                  AND NOT tier_column.attisdropped
                  AND strpos(
                      pg_catalog.pg_get_constraintdef(candidate.oid),
                      'vip_tier') > 0
            LOOP
                EXECUTE format(
                    'ALTER TABLE %I.%I DROP CONSTRAINT %I',
                    legacy_check.schema_name,
                    legacy_check.table_name,
                    legacy_check.constraint_name);
            END LOOP;
        END
        $donator_tier_cleanup$;

        ALTER TABLE public.accounts
            RENAME COLUMN vip_tier TO donator_tier;

        ALTER TABLE public.accounts
            RENAME COLUMN vip_expires_at TO donator_expires_at;

        ALTER TABLE public.accounts
            ADD CONSTRAINT ck_accounts_donator_tier
            CHECK (donator_tier BETWEEN 0 AND 5) NOT VALID;

        ALTER TABLE public.accounts
            VALIDATE CONSTRAINT ck_accounts_donator_tier;
        """);
}
