using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static bool IsCurrentHolySuitPolicy(HolySuitPolicySnapshot value) =>
        value.Tiers.SequenceEqual(HolySuitContentBaseline.Tiers) &&
        value.Upgrades.SequenceEqual(HolySuitContentBaseline.Upgrades) &&
        value.Consumables.SequenceEqual(HolySuitContentBaseline.Consumables) &&
        value.OperationPolicy == HolySuitContentBaseline.OperationPolicy;

    private static bool IsLegacyHolySuitPolicy(HolySuitPolicySnapshot value) =>
        value.Tiers.SequenceEqual(HolySuitContentBaselineV1.Tiers) &&
        value.Upgrades.SequenceEqual(HolySuitContentBaselineV1.Upgrades) &&
        value.Consumables.SequenceEqual(HolySuitContentBaselineV1.Consumables) &&
        value.OperationPolicy == HolySuitContentBaselineV1.OperationPolicy;

    private static async Task<V9PublicationSnapshot> ReconcileHolySuitDiviniumAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, V9PublicationSnapshot prior, CancellationToken token)
    {
        if (!IsCurrentHolySuitPolicy(prior.HolySuit) && !IsLegacyHolySuitPolicy(prior.HolySuit))
            throw new InvalidOperationException("Holy Suit policy is not the exact current or released seven-tier predecessor.");
        var reviewed = await ReadCanonicalReviewedHolySuitItemsAsync(connection, transaction, token);
        var predecessors = await ReadReviewedHolySuitPredecessorsAsync(connection, transaction, token);
        var byId = prior.Definitions.ToDictionary(item => item.Id);
        foreach (var target in reviewed)
        {
            if (byId.TryGetValue(target.Id, out var actual) && !DefinitionsEquivalent(actual, target) &&
                !MatchesReviewedHolySuitPredecessor(actual, predecessors))
                throw new InvalidOperationException($"Holy Suit item {target.Id} conflicts with its exact released predecessor.");
            byId[target.Id] = target;
        }
        return prior with
        {
            Definitions = byId.Values.OrderBy(item => item.Id).ToArray(),
            HolySuit = ReviewedHolySuitPolicy
        };
    }

    private static async Task UpgradeExactLegacyHolySuitMutableRowsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, IReadOnlyList<ItemTemplateDefinition> reviewed, CancellationToken token)
    {
        var predecessors = await ReadReviewedHolySuitPredecessorsAsync(connection, transaction, token);
        var current = reviewed.ToDictionary(item => item.Id);
        // Lock before comparing every field. The publication transaction then
        // cannot race an unrelated mutable edit between validation and rename.
        await using var read = new NpgsqlCommand("""
            SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,hand,skill_flag,
                   texture,icon,stats::text
            FROM public.item_templates WHERE id=ANY(@ids) ORDER BY id FOR UPDATE;
            """, connection, transaction);
        read.Parameters.AddWithValue("ids", HolySuitMutableCompatibilityItemIds);
        var renames = new List<ItemTemplateDefinition>();
        await using (var reader = await read.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var actual = ReadDefinition(reader);
                var target = current[actual.Id];
                if (DefinitionsEquivalent(actual, target)) continue;
                if (actual.Id is not (9014 or 9015 or 9016 or 9017 or 9025) ||
                    !MatchesReviewedHolySuitPredecessor(actual, predecessors))
                    throw new InvalidOperationException($"Mutable Holy Suit item {actual.Id} is not an exact recognized identity; no mutable row was overwritten.");
                renames.Add(target);
            }
        }
        foreach (var target in renames)
        {
            await using var update = new NpgsqlCommand(
                "UPDATE public.item_templates SET display_name=@name,texture=@texture,icon=@icon,stats=@stats::jsonb WHERE id=@id;",
                connection, transaction);
            update.Parameters.AddWithValue("name", target.DisplayName);
            update.Parameters.AddWithValue("texture", target.Texture);
            update.Parameters.AddWithValue("icon", target.Icon);
            update.Parameters.AddWithValue("stats", target.StatsJson);
            update.Parameters.AddWithValue("id", checked((int)target.Id));
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidDataException("A locked Holy Suit predecessor identity disappeared.");
        }
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>> ReadReviewedHolySuitPredecessorsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var original = await ReadCanonicalReviewedHolySuitItemsAsync(connection, transaction, token, legacy: true);
        var divinium = await ReadCanonicalReviewedHolySuitItemsAsync(connection, transaction, token,
            HolySuitContentBaselineV2.ItemTemplates);
        var materials = await ReadCanonicalReviewedHolySuitItemsAsync(connection, transaction, token,
            HolySuitContentBaselineV3.ItemTemplates);
        return original.Concat(divinium).Concat(materials).ToArray();
    }

    private static bool MatchesReviewedHolySuitPredecessor(ItemTemplateDefinition actual,
        IReadOnlyList<ItemTemplateDefinition> predecessors) =>
        predecessors.Any(old => old.Id == actual.Id && DefinitionsEquivalent(actual, old));

    private static async Task<bool> PublishedHolySuitItemsAreCurrentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string revision, CancellationToken token)
    {
        var reviewed = await ReadCanonicalReviewedHolySuitItemsAsync(connection, transaction, token);
        var published = await ReadHolySuitCompatibilityRowsAsync(connection, transaction,
            "item_template_content_definitions", revision, token);
        return published.Count == reviewed.Count &&
            published.Zip(reviewed).All(pair => DefinitionsEquivalent(pair.First, pair.Second));
    }
}
