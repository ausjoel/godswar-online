namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    internal static PostgresSchemaMigration
        CreateFactionCrierFoundation() => new(
        "20260821_100_faction_crier_foundation",
        "Create durable Faction Crier content, currency, and evidence",
        string.Concat(
            FactionCrierCurrencySql,
            "\n",
            FactionCrierItemSql,
            "\n",
            FactionCrierBalanceSql,
            "\n",
            FactionCrierEvidenceSql));
}
