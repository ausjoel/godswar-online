using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckLegacyInstanceDailyEntryMigration()
    {
        var ledgerMigration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260901_132_legacy_instance_daily_entry");
        Check.True(
            ledgerMigration.Sql.Contains(
                "legacy_instance_daily_entries",
                StringComparison.Ordinal) &&
            ledgerMigration.Sql.Contains(
                "instance_kind BETWEEN 1 AND 2",
                StringComparison.Ordinal) &&
            ledgerMigration.Sql.Contains(
                "PRIMARY KEY (",
                StringComparison.Ordinal) &&
            ledgerMigration.Sql.Contains(
                "reservation_id",
                StringComparison.Ordinal) &&
            ledgerMigration.Sql.Contains(
                "REFERENCES public.character_base(id) ON DELETE CASCADE",
                StringComparison.Ordinal),
            "legacy instance daily claims are realm/day/kind scoped, " +
            "party-releasable, and character-owned");

        var limitMigration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260901_133_legacy_instance_daily_entry_limit");
        Check.True(
            limitMigration.Sql.Contains(
                "legacy_instance_settings",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "daily_entry_limit BETWEEN 1 AND 99",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "free_entry_limit BETWEEN 0 AND daily_entry_limit",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "instance_kind <> 2 OR",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "free_entry_limit = daily_entry_limit",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "(1, 3, 1)",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "(2, 3, 3)",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "DROP CONSTRAINT legacy_instance_daily_entries_pkey",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "ADD CONSTRAINT legacy_instance_daily_entries_pkey",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "ADD COLUMN admitted_at timestamptz",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "SET admitted_at = claimed_at",
                StringComparison.Ordinal) &&
            limitMigration.Sql.Contains(
                "WHERE admitted_at IS NULL",
                StringComparison.Ordinal) &&
            !limitMigration.Sql.Contains(
                "DELETE FROM public.legacy_instance_daily_entries",
                StringComparison.OrdinalIgnoreCase),
            "Atlantis and Wonderland each default to three attempts; " +
            "only Atlantis retries require payment, while the upgraded " +
            "ledger preserves prior rows as admitted attempt one");

        var paidRetryMigration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260901_136_legacy_instance_paid_retries");
        Check.True(
            paidRetryMigration.Sql.Contains(
                "ADD COLUMN paid_retry_limit smallint",
                StringComparison.Ordinal) &&
            paidRetryMigration.Sql.Contains(
                "SET free_entry_limit = 3",
                StringComparison.Ordinal) &&
            paidRetryMigration.Sql.Contains(
                "WHEN 1 THEN 1",
                StringComparison.Ordinal) &&
            paidRetryMigration.Sql.Contains(
                "WHEN 2 THEN 0",
                StringComparison.Ordinal) &&
            paidRetryMigration.Sql.Contains(
                "DROP COLUMN daily_entry_limit",
                StringComparison.Ordinal) &&
            paidRetryMigration.Sql.Contains(
                "paid_retry_limit IS NULL OR",
                StringComparison.Ordinal) &&
            paidRetryMigration.Sql.Contains(
                "paid_retry_limit BETWEEN 0 AND 99",
                StringComparison.Ordinal) &&
            paidRetryMigration.Sql.Contains(
                "instance_kind <> 2 OR",
                StringComparison.Ordinal) &&
            paidRetryMigration.Sql.Contains(
                "paid_retry_limit = 0",
                StringComparison.Ordinal),
            "migration 136 upgrades Atlantis to three free entries plus " +
            "one paid retry, permits a database-owned unlimited Atlantis " +
            "policy, and keeps Wonderland free-only");

        var harborAttack = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260929_216_harbor_attack_daily_entry");
        Check.True(
            harborAttack.Sql.Contains(
                "legacy_instance_settings",
                StringComparison.Ordinal) &&
            harborAttack.Sql.Contains(
                "legacy_instance_daily_entries",
                StringComparison.Ordinal) &&
            harborAttack.Sql.Contains(
                "kind_column.attname = 'instance_kind'",
                StringComparison.Ordinal) &&
            harborAttack.Sql.Contains(
                "contype = 'c'",
                StringComparison.Ordinal) &&
            harborAttack.Sql.Contains(
                "CHECK (instance_kind BETWEEN 1 AND 3)",
                StringComparison.Ordinal) &&
            harborAttack.Sql.Contains(
                "VALUES (3, 1, 0)",
                StringComparison.Ordinal) &&
            !harborAttack.Sql.Contains(
                "DELETE FROM public.legacy_instance_daily_entries",
                StringComparison.OrdinalIgnoreCase) &&
            !harborAttack.Sql.Contains(
                "DROP TABLE",
                StringComparison.OrdinalIgnoreCase),
            "港湾遇袭 widens both instance-kind domains to a third kind and " +
            "seeds one free daily entry with no paid retry, without deleting " +
            "any existing claim");
    }
}
