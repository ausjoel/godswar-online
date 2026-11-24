using System.Data;
using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Loads the three GM monster tables into a validated
/// <see cref="MonsterOverrideSnapshot"/>.
/// </summary>
/// <remarks>
/// Same shape as the monster-loot reader: one repeatable-read transaction so the
/// three tables are read from a single point in time, explicit row bounds, and a
/// graceful empty result when the migration has not run yet - an operator's
/// older database must still boot.
/// </remarks>
internal static class PostgresMonsterOverrideSnapshotReader
{
    /// <summary>PostgreSQL <c>undefined_table</c>.</summary>
    private const string UndefinedTableSqlState = "42P01";

    private const int MaximumSpawnRows = 20_000;
    private const int MaximumEditRows = 200_000;
    private const int MaximumAttributeRows = 20_000;

    public static async Task<MonsterOverrideSnapshot> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        try
        {
            var spawns = await ReadSpawnsAsync(connection, transaction, cancellationToken);
            var edits = await ReadEditsAsync(connection, transaction, cancellationToken);
            var attributes = await ReadAttributesAsync(
                connection,
                transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MonsterOverrideSnapshot.Create(spawns, edits, attributes);
        }
        catch (PostgresException error) when (error.SqlState == UndefinedTableSqlState)
        {
            await transaction.RollbackAsync(cancellationToken);
            Console.WriteLine(
                "[monster-overrides] GM monster tables are absent; no operator " +
                "overrides are loaded. Start the server once more after the " +
                "migration, or ignore this on an older database.");
            return MonsterOverrideSnapshot.Empty;
        }
    }

    private static async Task<List<MonsterSpawnOverrideRow>> ReadSpawnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, name, waypoint_id, map_id, scene_key, template_key,
                   display_name, object_id, pos_x, pos_z, clear_bytes, enabled
            FROM gm_monster_spawns
            ORDER BY map_id, object_id
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("limit", MaximumSpawnRows);
        var rows = new List<MonsterSpawnOverrideRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MonsterSpawnOverrideRow(
                Id: reader.GetInt64(0),
                Name: reader.GetString(1),
                WaypointId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                MapId: reader.GetInt16(3),
                SceneKey: reader.GetString(4),
                TemplateKey: reader.GetString(5),
                DisplayName: reader.GetString(6),
                ObjectId: checked((uint)reader.GetInt64(7)),
                X: reader.GetFloat(8),
                Z: reader.GetFloat(9),
                Packet: (byte[])reader[10],
                Enabled: reader.GetBoolean(11)));
        }

        return rows;
    }

    private static async Task<
        List<((short MapId, uint ObjectId) Key, MonsterSpawnEdit Edit)>>
        ReadEditsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, object_id, enabled, pos_x, pos_z, facing,
                   level, current_health, maximum_health,
                   physical_attack, magic_attack, physical_defense, magic_defense,
                   hit, dodge, critical, critical_resistance
            FROM gm_monster_spawn_edits
            ORDER BY map_id, object_id
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("limit", MaximumEditRows);
        var rows = new List<((short, uint), MonsterSpawnEdit)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetInt16(0), checked((uint)reader.GetInt64(1)));
            rows.Add((key, new MonsterSpawnEdit(
                Enabled: reader.GetBoolean(2),
                X: OptionalFloat(reader, 3),
                Z: OptionalFloat(reader, 4),
                Facing: OptionalFloat(reader, 5),
                Attributes: new MonsterAttributeOverride(
                    Level: OptionalInt(reader, 6),
                    CurrentHealth: OptionalInt(reader, 7),
                    MaximumHealth: OptionalInt(reader, 8),
                    PhysicalAttack: OptionalInt(reader, 9),
                    MagicAttack: OptionalInt(reader, 10),
                    PhysicalDefense: OptionalInt(reader, 11),
                    MagicDefense: OptionalInt(reader, 12),
                    Hit: OptionalInt(reader, 13),
                    Dodge: OptionalInt(reader, 14),
                    Critical: OptionalInt(reader, 15),
                    CriticalResistance: OptionalInt(reader, 16)))));
        }

        return rows;
    }

    private static async Task<
        List<((short MapId, string TemplateKey) Key, MonsterAttributeOverride Override)>>
        ReadAttributesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        // W25 (the admin d152b): which reversible data step wrote a row's attack, the attack from before it (its backup table)
        // and the step-20 "suggested" low-level attack. The backup / suggested tables exist only after their data steps.
        bool b23, b25, sug;
        await using (var probe = new NpgsqlCommand(
            """
            SELECT coalesce(has_table_privilege(to_regclass('public.w23_monster_attack_backup'), 'SELECT'), false),
                   coalesce(has_table_privilege(to_regclass('public.w25_world_boss_attack_backup'), 'SELECT'), false),
                   coalesce(has_table_privilege(to_regclass('public.w25_suggested_attack'), 'SELECT'), false);
            """, connection, transaction))
        await using (var r = await probe.ExecuteReaderAsync(cancellationToken))
        {
            await r.ReadAsync(cancellationToken);
            (b23, b25, sug) = (r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2));
        }

        var sql = $"""
            SELECT a.map_id, a.template_key,
                   a.level, a.current_health, a.maximum_health,
                   a.physical_attack, a.magic_attack, a.physical_defense, a.magic_defense,
                   a.hit, a.dodge, a.critical, a.critical_resistance,
                   CASE WHEN a.updated_by = 'w23-monster-damage' OR a.note LIKE '%[W23 attack]%' THEN 'w23'
                        WHEN a.updated_by = 'w25-world-boss' OR a.note LIKE '%[W25 attack]%' THEN 'w25b' END AS step,
                   CASE WHEN a.updated_by = 'w23-monster-damage' OR a.note LIKE '%[W23 attack]%'
                        THEN {(b23 ? "b23.physical_attack" : "NULL::integer")}
                        ELSE {(b25 ? "b25.physical_attack" : "NULL::integer")} END AS off_pa,
                   CASE WHEN a.updated_by = 'w23-monster-damage' OR a.note LIKE '%[W23 attack]%'
                        THEN {(b23 ? "b23.magic_attack" : "NULL::integer")}
                        ELSE {(b25 ? "b25.magic_attack" : "NULL::integer")} END AS off_ma
            FROM gm_monster_attributes a
            {(b23 ? "LEFT JOIN public.w23_monster_attack_backup b23 ON b23.map_id = a.map_id AND b23.template_key = a.template_key" : "")}
            {(b25 ? "LEFT JOIN public.w25_world_boss_attack_backup b25 ON b25.map_id = a.map_id AND b25.template_key = a.template_key" : "")}
            ORDER BY a.map_id, a.template_key
            LIMIT @limit;
            """;
        var rows = new List<((short, string), MonsterAttributeOverride)>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("limit", MaximumAttributeRows);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetInt16(0), reader.GetString(1));
                rows.Add((key, new MonsterAttributeOverride(
                    Level: OptionalInt(reader, 2),
                    CurrentHealth: OptionalInt(reader, 3),
                    MaximumHealth: OptionalInt(reader, 4),
                    PhysicalAttack: OptionalInt(reader, 5),
                    MagicAttack: OptionalInt(reader, 6),
                    PhysicalDefense: OptionalInt(reader, 7),
                    MagicDefense: OptionalInt(reader, 8),
                    Hit: OptionalInt(reader, 9),
                    Dodge: OptionalInt(reader, 10),
                    Critical: OptionalInt(reader, 11),
                    CriticalResistance: OptionalInt(reader, 12),
                    AttackStep: reader.IsDBNull(13) ? null : reader.GetString(13),
                    OffPhysicalAttack: OptionalInt(reader, 14),
                    OffMagicAttack: OptionalInt(reader, 15))));
            }
        }

        if (sug)
        {
            var index = rows.Select(static (row, i) => (row.Item1, i)).ToDictionary(static x => x.Item1, static x => x.i);
            await using var command = new NpgsqlCommand(
                "SELECT map_id, template_key, physical_attack FROM public.w25_suggested_attack ORDER BY map_id, template_key LIMIT @limit;",
                connection, transaction);
            command.Parameters.AddWithValue("limit", MaximumAttributeRows);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetInt16(0), reader.GetString(1));
                var attack = reader.GetInt32(2);
                if (index.TryGetValue(key, out var at))
                {
                    rows[at] = (key, rows[at].Item2 with { SuggestedPhysicalAttack = attack });
                }
                else
                {
                    rows.Add((key, new MonsterAttributeOverride(SuggestedPhysicalAttack: attack)));
                }
            }
        }

        return rows;
    }

    /// <summary>NULL means "not configured" and must stay distinct from zero.</summary>
    private static int? OptionalInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static float? OptionalFloat(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFloat(ordinal);
}
