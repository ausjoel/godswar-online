using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReconcileReviewedOpalAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedOpalAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static definition => definition.Id);
        byId[reviewed.Id] = reviewed;
        return byId.Values.OrderBy(static definition => definition.Id)
            .ToArray();
    }

    private static async Task<bool> PublishedOpalIsCompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var expected = await ReadCanonicalReviewedOpalAsync(
            connection,
            transaction,
            cancellationToken);
        var actual = await ReadOpalAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            revision,
            cancellationToken);
        return actual is not null &&
            DefinitionsEquivalent(actual, expected);
    }

    private static async Task EnsureOpalMutableCompatibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var published = await ReadOpalAsync(
                connection,
                transaction,
                "item_template_content_definitions",
                revision,
                cancellationToken) ??
            throw new InvalidOperationException(
                "The published item content does not contain Opal 3932.");
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.item_templates (
                id, kind, name_key, display_name, equipment_slot,
                class_ids, min_level, max_level, hand, skill_flag,
                texture, icon, stats)
            SELECT
                id, kind, name_key, display_name, equipment_slot,
                class_ids, min_level, max_level, hand, skill_flag,
                texture, icon, stats
            FROM public.item_template_content_definitions
            WHERE revision = @revision AND id = @itemId
            ON CONFLICT (id) DO UPDATE
            SET kind = EXCLUDED.kind,
                name_key = EXCLUDED.name_key,
                display_name = EXCLUDED.display_name,
                equipment_slot = EXCLUDED.equipment_slot,
                class_ids = EXCLUDED.class_ids,
                min_level = EXCLUDED.min_level,
                max_level = EXCLUDED.max_level,
                hand = EXCLUDED.hand,
                skill_flag = EXCLUDED.skill_flag,
                texture = EXCLUDED.texture,
                icon = EXCLUDED.icon,
                stats = EXCLUDED.stats;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue(
                "itemId",
                LegacyInstanceOpalItemContentBaseline.ItemId);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var mutable = await ReadOpalAsync(
            connection,
            transaction,
            "item_templates",
            revision: null,
            cancellationToken);
        if (mutable is null ||
            !DefinitionsEquivalent(mutable, published))
        {
            throw new InvalidOperationException(
                "Mutable Opal 3932 conflicts with the sealed publication.");
        }
    }

    private static async Task<ItemTemplateDefinition>
        ReadCanonicalReviewedOpalAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seed = LegacyInstanceOpalItemContentBaseline.ItemTemplate;
        await using var command = new NpgsqlCommand(
            "SELECT @stats::jsonb::text;",
            connection,
            transaction);
        command.Parameters.Add(
            "stats",
            NpgsqlDbType.Text).Value = seed.StatsJson;
        var canonical = await command.ExecuteScalarAsync(cancellationToken)
            as string ?? throw new InvalidDataException(
                "Opal JSON canonicalization returned no value.");
        return ToDefinition(seed) with { StatsJson = canonical };
    }

    private static async Task<ItemTemplateDefinition?> ReadOpalAsync(
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
            WHERE {predicate}id = @itemId;
            """,
            connection,
            transaction);
        if (revision is not null)
        {
            command.Parameters.AddWithValue("revision", revision);
        }
        command.Parameters.AddWithValue(
            "itemId",
            LegacyInstanceOpalItemContentBaseline.ItemId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadDefinition(reader)
            : null;
    }
}
