using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task EnsureSocketSpellMutableCompatibilityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string revision, CancellationToken token)
    {
        var current = await ReadCanonicalReviewedSocketSpellsAsync(connection, transaction, token);
        var previous = await ReadCanonicalReviewedSocketSpellsAsync(connection, transaction, token, legacy: true);
        foreach (var expected in current)
        {
            await using (var read = new NpgsqlCommand("""
                SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,hand,
                    skill_flag,texture,icon,stats::text FROM item_templates WHERE id=@id FOR UPDATE;
                """, connection, transaction))
            {
                read.Parameters.AddWithValue("id", checked((int)expected.Id));
                await using var reader = await read.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token))
                {
                    var actual = ReadDefinition(reader);
                    if (!DefinitionsEquivalent(actual, expected) &&
                        !DefinitionsEquivalent(actual, previous.Single(item => item.Id == expected.Id)))
                        throw new InvalidOperationException(
                            $"Mutable Socket Spell {expected.Id} conflicts with reviewed artwork predecessors.");
                }
            }
            await using var update = new NpgsqlCommand("""
                INSERT INTO item_templates(id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                    hand,skill_flag,texture,icon,stats)
                SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,hand,skill_flag,texture,icon,stats
                FROM item_template_content_definitions WHERE revision=@revision AND id=@id
                ON CONFLICT(id) DO UPDATE SET texture=EXCLUDED.texture,icon=EXCLUDED.icon,stats=EXCLUDED.stats;
                """, connection, transaction);
            update.Parameters.AddWithValue("id", checked((int)expected.Id));
            update.Parameters.AddWithValue("revision", revision);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException($"Socket Spell {expected.Id} is missing from the reviewed publication.");
        }
    }
}
