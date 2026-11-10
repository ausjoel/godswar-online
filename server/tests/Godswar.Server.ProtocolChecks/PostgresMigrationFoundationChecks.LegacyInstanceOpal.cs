using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckLegacyInstanceOpalPaymentMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260901_135_legacy_instance_opal_payment");
        Check.True(
            migration.Sql.Contains("3932", StringComparison.Ordinal) &&
            migration.Sql.Contains("'Earphone3932'", StringComparison.Ordinal) &&
            migration.Sql.Contains("'Opal'", StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "legacy_instance_opal_payments",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "payment_status IN ('pending', 'committed', 'refunded')",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "charge_inventory_revision",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "refund_inventory_revision",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "charge_owner_id uuid NOT NULL",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "charge_owner_generation bigint NOT NULL",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                ") IS TRUE",
                StringComparison.Ordinal),
            "Opal migration publishes stock item 3932 and durable, " +
            "ownership-fenced, non-nullable compensation evidence");
    }
}
