using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private static async Task<IdentityReleaseState> ReadIdentityStateAsync(
        NpgsqlConnection connection,
        IdentityColumnProjection? previousProjection)
    {
        if (!await RelationExistsAsync(connection, "public.accounts") ||
            !await RelationExistsAsync(connection, "public.character_base"))
        {
            return new IdentityReleaseState(null, null, null);
        }

        var projection = new IdentityColumnProjection(
            await ReadIdentityColumnsAsync(connection, "accounts"),
            await ReadIdentityColumnsAsync(connection, "character_base"));
        var full = await ReadIdentityFingerprintAsync(connection, projection);
        // New columns are checked by current-schema idempotence. Upgrade
        // preservation compares every column that existed before migration.
        var projected = previousProjection is null
            ? full
            : await ReadIdentityFingerprintAsync(connection, previousProjection);
        return new IdentityReleaseState(full, projected, projection);
    }

    private static async Task<string[]> ReadIdentityColumnsAsync(
        NpgsqlConnection connection,
        string tableName)
    {
        await using var command = new NpgsqlCommand("""
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @tableName
            ORDER BY column_name;
            """, connection);
        command.Parameters.AddWithValue("tableName", tableName);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var column = reader.GetString(0);
            columns.Add(tableName == "accounts" ? column switch
            {
                "vip_tier" => "donator_tier",
                "vip_expires_at" => "donator_expires_at",
                _ => column
            } : column);
        }

        return columns.ToArray();
    }

    private static async Task<string> ReadIdentityFingerprintAsync(
        NpgsqlConnection connection,
        IdentityColumnProjection projection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT count(*)::text || ':' ||
                    md5(COALESCE(string_agg((
                        SELECT jsonb_object_agg(field.key, field.value)::text
                        FROM (
                            SELECT CASE key
                                WHEN 'vip_tier' THEN 'donator_tier'
                                WHEN 'vip_expires_at' THEN 'donator_expires_at'
                                ELSE key END AS key, value
                            FROM jsonb_each(to_jsonb(account_row))
                        ) field
                        WHERE field.key = ANY(@accountColumns)
                    ), '|' ORDER BY account_row.id), ''))
                 FROM public.accounts account_row) || '|' ||
                (SELECT count(*)::text || ':' ||
                    md5(COALESCE(string_agg((
                        SELECT jsonb_object_agg(field.key, field.value)::text
                        FROM jsonb_each(to_jsonb(character_row)) field
                        WHERE field.key = ANY(@characterColumns)
                    ), '|' ORDER BY character_row.id), ''))
                 FROM public.character_base character_row);
            """, connection);
        command.Parameters.AddWithValue("accountColumns", projection.AccountColumns);
        command.Parameters.AddWithValue("characterColumns", projection.CharacterColumns);
        return await command.ExecuteScalarAsync() as string
            ?? throw new InvalidDataException("Identity fingerprint returned no value.");
    }

    private sealed record IdentityColumnProjection(
        string[] AccountColumns,
        string[] CharacterColumns);

    private sealed record IdentityReleaseState(
        string? FullFingerprint,
        string? ProjectedFingerprint,
        IdentityColumnProjection? Projection);
}
