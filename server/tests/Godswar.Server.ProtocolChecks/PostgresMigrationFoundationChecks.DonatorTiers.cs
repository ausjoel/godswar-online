using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private const string DonatorTierMigrationId =
        "20260831_128_donator_tiers";

    private static void CheckDonatorTierMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            entry => entry.Id == DonatorTierMigrationId);
        var cleanup = migration.Sql.IndexOf(
            "DO $donator_tier_cleanup$",
            StringComparison.Ordinal);
        var definitionInspection = migration.Sql.IndexOf(
            "pg_catalog.pg_get_constraintdef(candidate.oid)",
            StringComparison.Ordinal);
        var quotedDrop = migration.Sql.IndexOf(
            "'ALTER TABLE %I.%I DROP CONSTRAINT %I'",
            StringComparison.Ordinal);
        var tierRename = migration.Sql.IndexOf(
            "RENAME COLUMN vip_tier TO donator_tier",
            StringComparison.Ordinal);

        Check.True(
            cleanup >= 0 &&
            definitionInspection > cleanup &&
            quotedDrop > definitionInspection &&
            tierRename > quotedDrop,
            "Donator migration dynamically removes every legacy tier " +
            "CHECK before renaming its referenced column");
        Check.True(
            migration.Sql.Contains(
                "schema_namespace.nspname = 'public'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "relation.relname = 'accounts'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "candidate.contype = 'c'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "tier_column.attnum = ANY(candidate.conkey)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "tier_column.attname = 'vip_tier'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "NOT tier_column.attisdropped",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'vip_tier') > 0",
                StringComparison.Ordinal),
            "legacy constraint discovery is limited to CHECK constraints " +
            "on public.accounts that depend on and render vip_tier");
        Check.True(
            migration.Sql.Contains(
                "legacy_check.schema_name",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "legacy_check.table_name",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "legacy_check.constraint_name",
                StringComparison.Ordinal) &&
            !migration.Sql.Contains(
                "DROP CONSTRAINT IF EXISTS accounts_vip_tier_check",
                StringComparison.Ordinal),
            "dynamic legacy CHECK removal identifier-quotes every catalog " +
            "name and does not assume one conventional constraint name");
        Check.True(
            migration.Sql.Contains(
                "RENAME COLUMN vip_expires_at TO donator_expires_at",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CHECK (donator_tier BETWEEN 0 AND 5)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "VALIDATE CONSTRAINT ck_accounts_donator_tier",
                StringComparison.Ordinal),
            "Donator migration preserves membership data and validates " +
            "the expanded finite tier domain");
    }
}
