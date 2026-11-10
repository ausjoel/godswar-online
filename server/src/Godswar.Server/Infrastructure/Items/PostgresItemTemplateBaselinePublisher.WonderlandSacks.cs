using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task<IReadOnlyList<ItemTemplateDefinition>> AppendWonderlandSacksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<ItemTemplateDefinition> prior, CancellationToken token)
    {
        var definitions = prior.ToDictionary(item => item.Id);
        foreach (var expected in await ReadCanonicalWonderlandSacksAsync(connection, transaction, token))
        {
            if (definitions.TryGetValue(expected.Id, out var actual) && !DefinitionsEquivalent(actual, expected))
                throw new InvalidOperationException($"Published Wonderland sack {expected.Id} conflicts with native content.");
            definitions[expected.Id] = expected;
        }
        return definitions.Values.OrderBy(item => item.Id).ToArray();
    }

    private static async Task<bool> PublishedWonderlandSacksAreCompleteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string revision, CancellationToken token)
    {
        var expected = await ReadCanonicalWonderlandSacksAsync(connection, transaction, token);
        var actual = new List<ItemTemplateDefinition>();
        await using var read = new NpgsqlCommand("""
            SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                hand,skill_flag,texture,icon,stats::text FROM item_template_content_definitions
            WHERE revision=@revision AND id BETWEEN 4450 AND 4461 ORDER BY id;
            """, connection, transaction);
        read.Parameters.AddWithValue("revision", revision);
        await using var reader = await read.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) actual.Add(ReadDefinition(reader));
        return actual.Count == expected.Count && actual.Zip(expected)
            .All(pair => DefinitionsEquivalent(pair.First, pair.Second));
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>> ReadCanonicalWonderlandSacksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var seeds = WonderlandSackItemContentBaseline.ItemTemplates;
        await using var command = new NpgsqlCommand("""
            SELECT input.item_id,input.stats::jsonb::text
            FROM unnest(@ids,@stats) AS input(item_id,stats) ORDER BY input.item_id;
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            { Value = seeds.Select(item => item.Id).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("stats", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { Value = seeds.Select(item => item.StatsJson).ToArray() });
        var canonical = new Dictionary<int, string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) canonical.Add(reader.GetInt32(0), reader.GetString(1));
        return seeds.Select(seed => ToDefinition(seed) with { StatsJson = canonical[seed.Id] }).ToArray();
    }

    private static async Task EnsureWonderlandSackMutableCompatibilityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string revision, CancellationToken token)
    {
        foreach (var expected in await ReadCanonicalWonderlandSacksAsync(connection, transaction, token))
        {
            await using (var read = new NpgsqlCommand("""
                SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                    hand,skill_flag,texture,icon,stats::text FROM item_templates WHERE id=@id FOR UPDATE;
                """, connection, transaction))
            {
                read.Parameters.AddWithValue("id", checked((int)expected.Id));
                await using var reader = await read.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token) && !DefinitionsEquivalent(ReadDefinition(reader), expected))
                    throw new InvalidOperationException($"Mutable Wonderland sack {expected.Id} conflicts with native content.");
            }
            await using var insert = new NpgsqlCommand("""
                INSERT INTO item_templates(id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                    hand,skill_flag,texture,icon,stats)
                SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                    hand,skill_flag,texture,icon,stats FROM item_template_content_definitions
                WHERE revision=@revision AND id=@id ON CONFLICT(id) DO NOTHING;
                """, connection, transaction);
            insert.Parameters.AddWithValue("revision", revision);
            insert.Parameters.AddWithValue("id", checked((int)expected.Id));
            await insert.ExecuteNonQueryAsync(token);
        }
    }
}
