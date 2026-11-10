using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task UpgradeReviewedHolyStoneMutableRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<ItemTemplateDefinition> published, CancellationToken token)
    {
        var legacy = await ReadCanonicalReviewedHolyStoneMaterialsAsync(
            connection, transaction, token, legacy: true);
        var mutable = await ReadHolyStoneMaterialRowsAsync(connection, transaction,
            "item_templates", revision: null, token);
        foreach (var row in mutable)
        {
            var expected = published.Single(item => item.Id == row.Id);
            if (DefinitionsEquivalent(row, expected)) continue;
            var previous = legacy.Single(item => item.Id == row.Id);
            if (!HolyStoneMaterialItemContentV3.ReagentIds.Contains(checked((int)row.Id)) ||
                !DefinitionsEquivalent(row, previous))
            {
                throw new InvalidOperationException(
                    $"Mutable Holy Stone item {row.Id} conflicts with reviewed reagent predecessors.");
            }
            await using var update = new NpgsqlCommand("""
                UPDATE item_templates
                SET display_name=@name, texture=@texture, icon=@icon, stats=@stats::jsonb
                WHERE id=@id;
                """, connection, transaction);
            update.Parameters.AddWithValue("id", checked((int)row.Id));
            update.Parameters.AddWithValue("name", expected.DisplayName);
            update.Parameters.AddWithValue("texture", expected.Texture!);
            update.Parameters.AddWithValue("icon", expected.Icon!);
            update.Parameters.AddWithValue("stats", expected.StatsJson);
            await update.ExecuteNonQueryAsync(token);
        }
    }
}
