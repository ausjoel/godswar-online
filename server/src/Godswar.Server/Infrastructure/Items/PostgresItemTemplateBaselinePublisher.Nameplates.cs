using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static readonly int[] NameplateCompatibilityItemIds =
        FactionCrierNameplateItemContentBaseline.ItemTemplates
            .Select(static value => value.Id)
            .OrderBy(static value => value)
            .ToArray();

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReconcileReviewedNameplatesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedNameplatesAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var definition in reviewed)
        {
            byId[definition.Id] = definition;
        }
        return byId.Values.OrderBy(static value => value.Id).ToArray();
    }

    private static async Task<bool> PublishedNameplatesAreCompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var expected = await ReadCanonicalReviewedNameplatesAsync(
            connection,
            transaction,
            cancellationToken);
        var actual = await ReadNameplateRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            revision,
            cancellationToken);
        return actual.Count == expected.Count &&
            actual.Zip(expected).All(static pair =>
                DefinitionsEquivalent(pair.First, pair.Second));
    }

    private static async Task EnsureNameplateMutableCompatibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var published = await ReadNameplateRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            revision,
            cancellationToken);
        var reviewed = await ReadCanonicalReviewedNameplatesAsync(
            connection,
            transaction,
            cancellationToken);
        ValidateNameplateRows(published, reviewed, "published revision");

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
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.Add(new NpgsqlParameter(
                "itemIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = NameplateCompatibilityItemIds
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mutable = await ReadNameplateRowsAsync(
            connection,
            transaction,
            "item_templates",
            revision: null,
            cancellationToken);
        ValidateNameplateRows(mutable, published, "mutable FK projection");
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalReviewedNameplatesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = FactionCrierNameplateItemContentBaseline.ItemTemplates
            .OrderBy(static value => value.Id)
            .ToArray();
        await using var command = new NpgsqlCommand(
            """
            SELECT input.item_id, input.stats::jsonb::text
            FROM unnest(@itemIds, @statsJson) AS input(item_id, stats)
            ORDER BY input.item_id;
            """,
            connection,
            transaction);
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
                "Reviewed Nameplate JSON canonicalization was incomplete.");
        }
        return seeds.Select(seed => ToDefinition(seed) with
        {
            StatsJson = canonicalStats[seed.Id]
        }).ToArray();
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadNameplateRowsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string table,
            string? revision,
            CancellationToken cancellationToken)
    {
        var predicate = revision is null
            ? string.Empty
            : "revision = @revision AND ";
        await using var command = new NpgsqlCommand(
            $"""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM {table}
            WHERE {predicate}id = ANY(@itemIds)
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
            Value = NameplateCompatibilityItemIds
        });
        var rows = new List<ItemTemplateDefinition>(
            NameplateCompatibilityItemIds.Length);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }

    private static void ValidateNameplateRows(
        IReadOnlyList<ItemTemplateDefinition> actual,
        IReadOnlyList<ItemTemplateDefinition> expected,
        string source)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Nameplate {source} contains {actual.Count} of " +
                $"{expected.Count} reviewed templates.");
        }
        for (var index = 0; index < expected.Count; index++)
        {
            if (!DefinitionsEquivalent(actual[index], expected[index]))
            {
                throw new InvalidOperationException(
                    $"Nameplate {expected[index].Id} conflicts with the " +
                    $"reviewed {source} definition.");
            }
        }
    }
}
