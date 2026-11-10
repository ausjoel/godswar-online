using System.Text.Json;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class PostgresItemAcquisitionPolicy
{
    internal static async Task<(short StackCap, short Bound)?>
        ReadLootItemPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        uint itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT stats::text FROM public.official_item_template_content " +
            "WHERE id = @itemId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", checked((int)itemId));
        var stats = await command.ExecuteScalarAsync(cancellationToken)
            as string;
        if (stats is null)
        {
            return null;
        }
        using var document = JsonDocument.Parse(stats);
        var root = document.RootElement;
        if (!TryReadPositiveShort(root, "Overlap", out var stackCap))
        {
            stackCap = 1;
        }
        var bound = root.TryGetProperty("BindType", out _)
            ? (short)1
            : (short)0;
        return (stackCap, bound);
    }

    private static bool TryReadPositiveShort(
        JsonElement root,
        string propertyName,
        out short value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }
        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt16(out value) &&
                                    value > 0,
            JsonValueKind.String => short.TryParse(
                property.GetString(),
                out value) && value > 0,
            _ => false
        };
    }

}
