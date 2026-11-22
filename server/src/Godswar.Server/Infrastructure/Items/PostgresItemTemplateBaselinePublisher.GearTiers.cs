using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

// B22 G1 (d34/d35/d37): the three GENERATED gear tiers past Class Suit IV (Titan Lv170, Primordial Lv185,
// Aether Lv200; GearTierItemSeed.Generated.cs from tools/b22-g1) and weapon ranks 10-14 with their own effects.
// Published as the item-content family "gear-tiers-v1"; same reuse/republish contract as the client catalog.
internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static readonly int[] GearTierItemIds =
        GearTierItemSeeds.All
            .Select(static value => value.Id)
            .OrderBy(static value => value)
            .ToArray();

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReconcileReviewedGearTierItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedGearTierItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var definition in reviewed)
        {
            // The reviewed gear-tier set owns the final metadata for these
            // identities; replace any historical definition and append the
            // identities that the prior release never carried.
            byId[definition.Id] = definition;
        }

        return byId.Values
            .OrderBy(static value => value.Id)
            .ToArray();
    }

    private static async Task<bool> PublishedGearTierItemsAreCompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var expected = await ReadCanonicalReviewedGearTierItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var actual = await ReadGearTierItemRowsAsync(
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
        ReadCanonicalReviewedGearTierItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = GearTierItemSeeds.All
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
                "Reviewed gear-tier JSON canonicalization was incomplete.");
        }

        return seeds.Select(seed => ToDefinition(seed) with
        {
            StatsJson = canonicalStats[seed.Id]
        })
            .ToArray();
    }

    private static async Task EnsureGearTierMutableTemplateCompatibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string publishedRevision,
        CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedGearTierItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var published = await ReadGearTierItemRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            publishedRevision,
            cancellationToken);
        ValidateGearTierItemRows(
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
                Value = GearTierItemIds
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mutable = await ReadGearTierItemRowsAsync(
            connection,
            transaction,
            "item_templates",
            revision: null,
            cancellationToken);
        ValidateGearTierItemRows(
            mutable,
            published,
            "mutable item-template FK projection");
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadGearTierItemRowsAsync(
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
            Value = GearTierItemIds
        });

        var rows = new List<ItemTemplateDefinition>(
            GearTierItemIds.Length);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }

    private static void ValidateGearTierItemRows(
        IReadOnlyList<ItemTemplateDefinition> actual,
        IReadOnlyList<ItemTemplateDefinition> expected,
        string source)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Gear-tier {source} contains {actual.Count} of " +
                $"{expected.Count} reviewed templates.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!DefinitionsEquivalent(actual[index], expected[index]))
            {
                throw new InvalidOperationException(
                    $"Gear-tier item {expected[index].Id} conflicts " +
                    $"with the reviewed {source} definition.");
            }
        }
    }

    // B22 G1 (d37): weapon ranks 10-14 match armour 14. Thresholds and effect ids are the client's
    // ArmEffFraction / ArmEff vectors written by tools/b22-g1/gen_ranks.py (keep both in step).
    // Rank 10 moves from the rank-9 effect (9) to its own effect 10.
    private const string GearTierWeaponRankSource = "b22_gear_tiers.ArmEffFraction";

    private static readonly EquipmentRankDefinition[] GearTierWeaponRanks =
    [
        new("weapon", 10, 8000, 10, GearTierWeaponRankSource),
        new("weapon", 11, 10000, 11, GearTierWeaponRankSource),
        new("weapon", 12, 12000, 12, GearTierWeaponRankSource),
        new("weapon", 13, 13500, 13, GearTierWeaponRankSource),
        new("weapon", 14, 16000, 14, GearTierWeaponRankSource)
    ];

    private static IReadOnlyList<EquipmentRankDefinition> ReconcileGearTierWeaponRanks(
        IReadOnlyList<EquipmentRankDefinition> prior)
    {
        return prior
            .Where(static rank =>
                !(rank.RankKind == "weapon" && rank.RankLevel >= 10))
            .Concat(GearTierWeaponRanks)
            .OrderBy(static rank => rank.RankKind, StringComparer.Ordinal)
            .ThenBy(static rank => rank.RankLevel)
            .ToArray();
    }

    private static async Task<bool> PublishedGearTierWeaponRanksAreCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var published = await ReadEquipmentRanksAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        var weapon = published
            .Where(static rank =>
                rank.RankKind == "weapon" && rank.RankLevel >= 10)
            .OrderBy(static rank => rank.RankLevel)
            .ToArray();
        return weapon.SequenceEqual(GearTierWeaponRanks);
    }

    private static async Task EnsureGearTierMutableRankCompatibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        // Keep the mutable staging rules in step so a fresh-baseline publication carries the same ranks.
        foreach (var rank in GearTierWeaponRanks)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO equipment_rank_rules (
                    rank_kind, rank_level, required_score, aura_effect, source)
                VALUES (@kind, @level, @score, @aura, @source)
                ON CONFLICT (rank_kind, rank_level) DO UPDATE
                SET required_score = EXCLUDED.required_score,
                    aura_effect = EXCLUDED.aura_effect,
                    source = EXCLUDED.source;
                """, connection, transaction);
            command.Parameters.AddWithValue("kind", rank.RankKind);
            command.Parameters.AddWithValue("level", rank.RankLevel);
            command.Parameters.AddWithValue("score", rank.RequiredScore);
            command.Parameters.AddWithValue("aura", rank.AuraEffect);
            command.Parameters.AddWithValue("source", rank.Source);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
