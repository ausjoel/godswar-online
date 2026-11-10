using System.Data;
using Godswar.Server.Application.World.Content;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Loads one repeatable-read snapshot of the adjustable monster loot tables.
/// Schema creation remains exclusively migration-owned, exactly like the
/// Medusa monster content reader.
/// </summary>
internal sealed class PostgresMonsterLootContentSnapshotReader
{
    private const int MaximumTables = 4_096;
    private const int MaximumRules = 32_768;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresMonsterLootContentSnapshotReader(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
    }

    public static async Task<MonsterLootContentSnapshot> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await new PostgresMonsterLootContentSnapshotReader(dataSource)
            .ReadAsync(cancellationToken);
    }

    public async Task<MonsterLootContentSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var tables = await ReadTablesAsync(
            connection,
            transaction,
            cancellationToken);
        // ATTR: roll profiles exist only after the attribute migration; without
        // it the stock query runs unchanged.
        var hasRollProfiles = await HasRollProfilesAsync(
            connection,
            transaction,
            cancellationToken);
        var loot = await ReadLootAsync(
            connection,
            transaction,
            hasRollProfiles,
            cancellationToken);
        var rollContent = hasRollProfiles
            ? await ReadRollContentAsync(
                connection,
                transaction,
                loot,
                cancellationToken)
            : LootRollContent.Empty;
        var snapshot = new MonsterLootContentSnapshot(tables, loot, rollContent);
        if (hasRollProfiles)
        {
            var profiled = loot.Count(static rule => rule.RollProfile is not null);
            var unresolved = loot.Count(rule =>
                rule.RollProfile is not null &&
                (!rollContent.TryGetProfile(rule.RollProfile, out _) ||
                    rollContent.StatTypeCount(rule.ItemId) == 0));
            Console.WriteLine(
                $"[loot] roll profiles: {rollContent.ProfileCount} profiles, " +
                $"{profiled} profile rules, {rollContent.PoolCount} item pools, " +
                $"{unresolved} rules fall back to their fixed columns");
        }
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static async Task<IReadOnlyList<MonsterLootTableRule>>
        ReadTablesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT template_key, maximum_drops
            FROM public.monster_loot_tables
            WHERE enabled
            ORDER BY template_key
            LIMIT @maximumRows;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("maximumRows", MaximumTables);
        var result = new List<MonsterLootTableRule>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetInt16(1)));
        }
        return result.AsReadOnly();
    }

    /// <summary>An attribute column, where <c>NULL</c> means "not configured".</summary>
    private static short? Optional(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);

    private static async Task<IReadOnlyList<MonsterLootRule>>
        ReadLootAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            bool hasRollProfiles,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT rule.template_key, rule.loot_index, rule.item_id,
                   rule.chance_basis_points, rule.minimum_quantity,
                   rule.maximum_quantity, rule.bound_on_pickup,
                   rule.item_quality, rule.item_grade,
                   rule.attribute1, rule.attribute_level1,
                   rule.attribute2, rule.attribute_level2,
                   rule.attribute3, rule.attribute_level3,
                   rule.attribute4, rule.attribute_level4,
                   rule.attribute5, rule.attribute_level5
            """ + (hasRollProfiles ? ", rule.roll_profile" : string.Empty) +
            """

            FROM public.monster_loot_rules rule
            JOIN public.monster_loot_tables header
              ON header.template_key = rule.template_key
            WHERE rule.enabled AND header.enabled
            ORDER BY rule.template_key, rule.loot_index
            LIMIT @maximumRows;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("maximumRows", MaximumRules);
        var result = new List<MonsterLootRule>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetInt16(1),
                checked((uint)reader.GetInt32(2)),
                reader.GetInt32(3),
                reader.GetInt16(4),
                reader.GetInt16(5),
                reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                new Godswar.Server.State.ItemGrantAttributes(
                    reader.IsDBNull(7) ? (short)1 : reader.GetInt16(7),
                    reader.IsDBNull(8) ? (short)1 : reader.GetInt16(8),
                    Optional(reader, 9), Optional(reader, 10),
                    Optional(reader, 11), Optional(reader, 12),
                    Optional(reader, 13), Optional(reader, 14),
                    Optional(reader, 15), Optional(reader, 16),
                    Optional(reader, 17), Optional(reader, 18)),
                hasRollProfiles && !reader.IsDBNull(19)
                    ? reader.GetString(19)
                    : null));
        }
        return result.AsReadOnly();
    }

    // ATTR: gear kinds whose MainAttribute list is a drop attribute pool.
    // Mount gear, fashion and consumables also carry lists; they never roll.
    private static readonly string[] EquipmentKinds =
    [
        "weapon", "shield", "armor", "cloth", "head", "cuff", "leggins",
        "glove", "shoes", "girdle", "amulet", "ring"
    ];

    private static async Task<bool> HasRollProfilesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT to_regclass('public.loot_roll_profiles') IS NOT NULL
               AND to_regclass('public.item_allowed_attributes') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'public'
                     AND table_name = 'monster_loot_rules'
                     AND column_name = 'roll_profile');
            """,
            connection,
            transaction);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<LootRollContent> ReadRollContentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<MonsterLootRule> loot,
        CancellationToken cancellationToken)
    {
        var profiles = new List<LootRollProfile>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT profile_key,
                   attribute_weight0, attribute_weight1, attribute_weight2,
                   attribute_weight3, attribute_weight4,
                   quality_min, quality_max, quality_step_bp,
                   grade_min, grade_max, grade_step_bp
            FROM public.loot_roll_profiles
            WHERE enabled
            ORDER BY profile_key;
            """,
            connection,
            transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                profiles.Add(new(
                    reader.GetString(0),
                    reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5),
                    reader.GetInt16(6), reader.GetInt16(7), reader.GetInt32(8),
                    reader.GetInt16(9), reader.GetInt16(10), reader.GetInt32(11)));
            }
        }

        var itemIds = loot
            .Where(static rule => rule.RollProfile is not null)
            .Select(static rule => checked((int)rule.ItemId))
            .Distinct()
            .ToArray();
        var pools = new Dictionary<uint, IReadOnlyList<LootAttributeOption>>();
        if (itemIds.Length > 0)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT item_id, attribute_id, stat_type
                FROM public.item_allowed_attributes
                WHERE item_id = ANY(@itemIds)
                  AND kind = ANY(@kinds)
                  AND attribute_id BETWEEN 0 AND 32767
                  AND stat_type IS NOT NULL
                ORDER BY item_id, position;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("itemIds", itemIds);
            command.Parameters.AddWithValue("kinds", EquipmentKinds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var itemId = checked((uint)reader.GetInt32(0));
                if (!pools.TryGetValue(itemId, out var list))
                {
                    pools[itemId] = list = new List<LootAttributeOption>();
                }

                ((List<LootAttributeOption>)list).Add(new(
                    checked((short)reader.GetInt32(1)),
                    reader.GetInt16(2)));
            }
        }

        // A bad profile row must not stop the server: skip it, and its rules
        // keep their fixed columns.
        foreach (var invalid in profiles.Where(static profile => !profile.IsValid))
        {
            Console.WriteLine(
                $"[loot] roll profile {invalid.ProfileKey} is invalid and ignored");
        }

        return new LootRollContent(
            profiles.Where(static profile => profile.IsValid).ToArray(),
            pools);
    }
}
