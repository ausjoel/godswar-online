using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static readonly int[] ClientCatalogItemIds =
        ClientCatalogItemContentBaseline.ItemTemplates
            .Select(static value => value.Id)
            .OrderBy(static value => value)
            .ToArray();

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReconcileReviewedClientCatalogItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedClientCatalogItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var definition in reviewed)
        {
            // The reviewed client catalog owns the final metadata for these
            // identities; replace any historical definition and append the
            // identities that the prior release never carried.
            byId[definition.Id] = definition;
        }

        return byId.Values
            .OrderBy(static value => value.Id)
            .ToArray();
    }

    private static async Task<bool> PublishedClientCatalogItemsAreCompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var expected = await ReadCanonicalReviewedClientCatalogItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var actual = await ReadClientCatalogItemRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            revision,
            cancellationToken);
        return actual.Count == expected.Count &&
            actual.Zip(expected).All(static pair =>
                DefinitionsEquivalent(pair.First, pair.Second));
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalReviewedClientCatalogItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = ClientCatalogItemContentBaseline.ItemTemplates
            .OrderBy(static value => value.Id)
            .ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT input.item_id, input.stats::jsonb::text
            FROM unnest(@itemIds, @statsJson) AS input(item_id, stats)
            ORDER BY input.item_id;
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = seeds.Select(static value => value.Id).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter(
            "statsJson",
            NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = seeds.Select(static value => value.StatsJson).ToArray()
        });
        var canonicalStats = new Dictionary<int, string>(seeds.Length);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            canonicalStats.Add(reader.GetInt32(0), reader.GetString(1));
        }

        if (canonicalStats.Count != seeds.Length)
        {
            throw new InvalidDataException(
                "Reviewed client-catalog JSON canonicalization was incomplete.");
        }

        return seeds.Select(seed => ToDefinition(seed) with
        {
            StatsJson = canonicalStats[seed.Id]
        })
            .ToArray();
    }

    private static async Task EnsureClientCatalogMutableTemplateCompatibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string publishedRevision,
        CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedClientCatalogItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var published = await ReadClientCatalogItemRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            publishedRevision,
            cancellationToken);
        ValidateClientCatalogItemRows(
            published,
            reviewed,
            $"published revision {publishedRevision}");

        // character_items retains an FK to the mutable compatibility table.
        // Project the missing reviewed identities without overwriting local
        // rows; conflicting metadata fails closed during validation below.
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO item_templates (
                id, kind, name_key, display_name, equipment_slot, class_ids,
                min_level, max_level, hand, skill_flag, texture, icon, stats)
            SELECT id, kind, name_key, display_name, equipment_slot, class_ids,
                   min_level, max_level, hand, skill_flag, texture, icon, stats
            FROM item_template_content_definitions
            WHERE revision = @revision
              AND id = ANY(@itemIds)
            ORDER BY id
            ON CONFLICT (id) DO NOTHING;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("revision", publishedRevision);
            command.Parameters.Add(new NpgsqlParameter(
                "itemIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = ClientCatalogItemIds
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mutable = await ReadClientCatalogItemRowsAsync(
            connection,
            transaction,
            "item_templates",
            revision: null,
            cancellationToken);
        ValidateClientCatalogItemRows(
            mutable,
            published,
            "mutable item-template FK projection");
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadClientCatalogItemRowsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string table,
            string? revision,
            CancellationToken cancellationToken)
    {
        var revisionPredicate = revision is null
            ? string.Empty
            : "revision = @revision AND ";
        await using var command = new NpgsqlCommand(
            $"""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM {table}
            WHERE {revisionPredicate}id = ANY(@itemIds)
            ORDER BY id;
            """,
            connection,
            transaction);
        if (revision is not null)
        {
            command.Parameters.AddWithValue("revision", revision);
        }
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = ClientCatalogItemIds
        });

        var rows = new List<ItemTemplateDefinition>(
            ClientCatalogItemIds.Length);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }

    private static void ValidateClientCatalogItemRows(
        IReadOnlyList<ItemTemplateDefinition> actual,
        IReadOnlyList<ItemTemplateDefinition> expected,
        string source)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Client-catalog {source} contains {actual.Count} of " +
                $"{expected.Count} reviewed templates.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!DefinitionsEquivalent(actual[index], expected[index]))
            {
                throw new InvalidOperationException(
                    $"Client-catalog item {expected[index].Id} conflicts " +
                    $"with the reviewed {source} definition.");
            }
        }
    }
}
