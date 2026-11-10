using Npgsql;
using NpgsqlTypes;

namespace Godswar.LootTool;

/// <summary>
/// Direct PostgreSQL access to the database-owned monster loot tables
/// (<c>monster_loot_tables</c> / <c>monster_loot_rules</c>), shaped after
/// <c>tools/verify_monster_loot_migration.sql</c>.
/// </summary>
internal sealed class LootStore : IAsyncDisposable
{
    /// <summary>服务端迁移 20260927_213 加的三态列。</summary>
    private const string BoundOnPickupColumn = "bound_on_pickup";

    private const string UndefinedTableSqlState = "42P01";

    private NpgsqlDataSource? _dataSource;

    private NpgsqlDataSource Source =>
        _dataSource ?? throw new InvalidOperationException("尚未连接数据库。");

    public bool IsConnected => _dataSource is not null;

    public string DatabaseName { get; private set; } = string.Empty;

    public void Connect(string connectionString)
    {
        Disconnect();
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        DatabaseName = new NpgsqlConnectionStringBuilder(connectionString).Database
            ?? string.Empty;
    }

    public void Disconnect()
    {
        _dataSource?.Dispose();
        _dataSource = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }
    }

    /// <summary>One aggregated row per loot key, with its current loot status.</summary>
    public async Task<List<MonsterRow>> LoadMonstersAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH active AS (
                SELECT revision
                  FROM public.monster_content_publication
                 WHERE family = 'monsters'
            ),
            spawns AS (
                SELECT definition.template_key,
                       count(*)::int AS spawn_count
                  FROM public.monster_spawn_definitions definition
                 WHERE definition.revision = (SELECT revision FROM active)
                 GROUP BY definition.template_key
            )
            SELECT mt.template_key,
                   min(mt.display_name) AS english_name,
                   CASE WHEN bool_or(mt.is_boss) THEN 'boss'
                        WHEN bool_or(mt.is_elite) THEN 'elite'
                        ELSE 'normal' END AS rank,
                   string_agg(DISTINCT mt.scene_key, ', ') AS scenes,
                   string_agg(DISTINCT mt.source_map_id::text, ', ') AS maps,
                   (lt.template_key IS NOT NULL) AS has_table,
                   COALESCE(lt.maximum_drops, 0)::smallint AS maximum_drops,
                   (SELECT count(*)::int
                      FROM public.monster_loot_rules r
                     WHERE r.template_key = mt.template_key) AS rule_count,
                   COALESCE(lt.enabled, false) AS enabled,
                   COALESCE(sp.spawn_count, 0) AS spawn_count
              FROM public.monster_templates mt
              LEFT JOIN public.monster_loot_tables lt
                ON lt.template_key = mt.template_key
              LEFT JOIN spawns sp
                ON sp.template_key = mt.template_key
             GROUP BY mt.template_key, lt.template_key, lt.maximum_drops,
                      lt.enabled, sp.spawn_count
             ORDER BY mt.template_key;
            """;

        await using var command = Source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<MonsterRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MonsterRow(
                reader.GetString(0),
                string.Empty,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetInt16(6),
                reader.GetInt32(7),
                reader.GetBoolean(8),
                reader.GetInt32(9)));
        }

        return rows;
    }

    public async Task<LootHeader?> LoadHeaderAsync(
        string templateKey,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT maximum_drops, enabled
              FROM public.monster_loot_tables
             WHERE template_key = @templateKey;
            """;
        await using var command = Source.CreateCommand(sql);
        command.Parameters.AddWithValue("templateKey", templateKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LootHeader(reader.GetInt16(0), reader.GetBoolean(1))
            : null;
    }

    public async Task<List<LootRule>> LoadRulesAsync(
        string templateKey,
        CancellationToken cancellationToken = default)
    {
        // 拾取后绑定与「物品属性」那 12 列都是服务端迁移加的：旧库没有它们时按 NULL 读，
        // 界面对应的列/按钮会隐藏（见 MainForm），而不是假装它们是 0/false。
        var columns = await LoadOptionalColumnsAsync(cancellationToken);
        var hasBoundOnPickup = columns.Contains(BoundOnPickupColumn);
        var hasAttributes = ItemAttributeColumns.HasAll(columns);

        var select = new List<string>
        {
            "loot_index",
            "item_id",
            "chance_basis_points",
            "minimum_quantity",
            "maximum_quantity",
            "enabled",
            hasBoundOnPickup ? BoundOnPickupColumn : "NULL::boolean"
        };
        select.AddRange(ItemAttributeColumns.Names.Select(
            column => hasAttributes ? column : "NULL::smallint"));

        var sql = $"""
            SELECT {string.Join(", ", select)}
              FROM public.monster_loot_rules
             WHERE template_key = @templateKey
             ORDER BY loot_index;
            """;
        await using var command = Source.CreateCommand(sql);
        command.Parameters.AddWithValue("templateKey", templateKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rules = new List<LootRule>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new LootRule(
                reader.GetInt16(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt16(3),
                reader.GetInt16(4),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                ItemAttributeColumns.Read(reader, 7)));
        }

        return rules;
    }

    /// <summary>
    /// <c>monster_loot_rules.bound_on_pickup</c> 在不在（服务端迁移
    /// <c>20260927_213_monster_loot_bind_on_pickup</c> 加的）。
    /// 旧库没有这一列时界面把「拾取后」列隐藏，保存也不会去碰它。
    /// </summary>
    public async Task<bool> HasBoundOnPickupColumnAsync(
        CancellationToken cancellationToken = default) =>
        (await LoadOptionalColumnsAsync(cancellationToken)).Contains(BoundOnPickupColumn);

    /// <summary>
    /// 那 12 列「物品属性」在不在（服务端迁移
    /// <c>20260927_214</c> 加的）。缺任何一列就整体当作没有：界面隐藏「属性…」，
    /// 保存时配过属性的规则会被拦住，不静默按默认值写。
    /// </summary>
    public async Task<bool> HasItemAttributeColumnsAsync(
        CancellationToken cancellationToken = default) =>
        ItemAttributeColumns.HasAll(await LoadOptionalColumnsAsync(cancellationToken));

    /// <summary>服务端认定的附加属性清单（id / 名称键 / 最高等级），给属性下拉用。</summary>
    public async Task<List<ItemAttributeTemplateRow>> LoadItemAttributeTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, name_key, max_level, percent
              FROM public.item_attribute_templates
             ORDER BY id;
            """;
        try
        {
            await using var command = Source.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<ItemAttributeTemplateRow>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ItemAttributeTemplateRow(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.GetBoolean(3)));
            }

            return rows;
        }
        catch (PostgresException ex) when (ex.SqlState == UndefinedTableSqlState)
        {
            // 旧库还没建这张表：属性名退化到客户端 XML，不挡着用
            return [];
        }
    }

    private async Task<HashSet<string>> LoadOptionalColumnsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name
              FROM information_schema.columns
             WHERE table_schema = 'public'
               AND table_name = 'monster_loot_rules';
            """;
        await using var command = Source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>
    /// 按库里实际有哪些可选列拼 upsert：旧库少列时那条 SQL 里就不出现它，
    /// 免得拿列名去撞「列不存在」。
    /// </summary>
    private static string BuildRuleUpsertSql(bool hasBoundOnPickup, bool hasAttributes)
    {
        var columns = new List<string>
        {
            "template_key",
            "loot_index",
            "item_id",
            "chance_basis_points",
            "minimum_quantity",
            "maximum_quantity",
            "enabled"
        };
        var values = new List<string>
        {
            "@templateKey",
            "@lootIndex",
            "@itemId",
            "@chance",
            "@minimum",
            "@maximum",
            "@enabled"
        };
        var updates = new List<string>
        {
            "item_id = EXCLUDED.item_id",
            "chance_basis_points = EXCLUDED.chance_basis_points",
            "minimum_quantity = EXCLUDED.minimum_quantity",
            "maximum_quantity = EXCLUDED.maximum_quantity",
            "enabled = EXCLUDED.enabled"
        };
        if (hasBoundOnPickup)
        {
            columns.Add(BoundOnPickupColumn);
            values.Add("@bound");
            updates.Add($"{BoundOnPickupColumn} = EXCLUDED.{BoundOnPickupColumn}");
        }

        if (hasAttributes)
        {
            foreach (var column in ItemAttributeColumns.Names)
            {
                columns.Add(column);
                values.Add("@" + column);
                updates.Add($"{column} = EXCLUDED.{column}");
            }
        }

        columns.Add("updated_at");
        values.Add("now()");
        return $"""
            INSERT INTO public.monster_loot_rules ({string.Join(", ", columns)})
            VALUES ({string.Join(", ", values)})
            ON CONFLICT (template_key, loot_index) DO UPDATE
               SET {string.Join(",\n                   ", updates)},
                   updated_at = now();
            """;
    }

    /// <summary>
    /// Writes a header plus its complete rule set in one transaction. Rules that
    /// vanished from the grid are deleted; everything else is upserted.
    /// </summary>
    /// <remarks>
    /// 「拾取后绑定」是三态：NULL 必须原样写成 NULL（跟随物品模板），不能落成 false。
    /// 12 列「物品属性」同理，NULL = 不配置。旧库还没有这些列时，只要界面上没配过就不写
    /// （写 NULL 等于什么都不改），一旦配过就直接报错，绝不静默按默认值写。
    /// </remarks>
    public async Task SaveLootAsync(
        string templateKey,
        short maximumDrops,
        bool enabled,
        IReadOnlyList<LootRuleInput> rules,
        CancellationToken cancellationToken = default)
    {
        ValidateLoot(templateKey, maximumDrops, rules);

        var columns = await LoadOptionalColumnsAsync(cancellationToken);
        var hasBoundOnPickup = columns.Contains(BoundOnPickupColumn);
        var hasAttributes = ItemAttributeColumns.HasAll(columns);
        if (!hasBoundOnPickup &&
            rules.Any(static rule => rule.BoundOnPickup is not null))
        {
            throw new LootValidationException(
                "数据库的 monster_loot_rules 还没有 bound_on_pickup 列" +
                "（服务端迁移 20260927_213 未应用），写不了「可交易 / 拾取绑定」。" +
                "请先重启一次游戏服务端让它跑迁移，或把这些规则改回「跟随物品(默认)」。");
        }

        if (!hasAttributes &&
            rules.Any(static rule => rule.Attributes is { IsEmpty: false }))
        {
            throw new LootValidationException(
                "数据库的 monster_loot_rules 还没有那 12 列物品属性" +
                "（服务端迁移 20260927_214 未应用），写不了品质/等级/附加属性。" +
                "请先重启一次游戏服务端让它跑迁移，或把这些规则的属性清空。");
        }

        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var header = new NpgsqlCommand(
            """
            INSERT INTO public.monster_loot_tables (
                template_key, maximum_drops, enabled, updated_at)
            VALUES (@templateKey, @maximumDrops, @enabled, now())
            ON CONFLICT (template_key) DO UPDATE
               SET maximum_drops = EXCLUDED.maximum_drops,
                   enabled = EXCLUDED.enabled,
                   updated_at = now();
            """,
            connection,
            transaction))
        {
            header.Parameters.AddWithValue("templateKey", templateKey);
            header.Parameters.AddWithValue("maximumDrops", maximumDrops);
            header.Parameters.AddWithValue("enabled", enabled);
            await header.ExecuteNonQueryAsync(cancellationToken);
        }

        var upsertSql = BuildRuleUpsertSql(hasBoundOnPickup, hasAttributes);

        foreach (var rule in rules)
        {
            await using var upsert = new NpgsqlCommand(
                upsertSql,
                connection,
                transaction);
            upsert.Parameters.AddWithValue("templateKey", templateKey);
            upsert.Parameters.AddWithValue("lootIndex", rule.LootIndex);
            upsert.Parameters.AddWithValue("itemId", rule.ItemId);
            upsert.Parameters.AddWithValue("chance", rule.ChanceBasisPoints);
            upsert.Parameters.AddWithValue("minimum", rule.MinimumQuantity);
            upsert.Parameters.AddWithValue("maximum", rule.MaximumQuantity);
            upsert.Parameters.AddWithValue("enabled", rule.Enabled);
            if (hasBoundOnPickup)
            {
                // 显式声明类型，好让 null 以 boolean 的 NULL 发出去（否则 Npgsql 推不出类型）
                upsert.Parameters.Add(new NpgsqlParameter(
                    "bound",
                    NpgsqlDbType.Boolean)
                {
                    Value = rule.BoundOnPickup is { } bound
                        ? bound
                        : DBNull.Value
                });
            }

            if (hasAttributes)
            {
                ItemAttributeColumns.AddParameters(upsert, rule.Attributes);
            }

            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var prune = new NpgsqlCommand(
            """
            DELETE FROM public.monster_loot_rules
             WHERE template_key = @templateKey
               AND loot_index <> ALL(@keep);
            """,
            connection,
            transaction))
        {
            prune.Parameters.AddWithValue("templateKey", templateKey);
            prune.Parameters.AddWithValue(
                "keep",
                rules.Select(static rule => rule.LootIndex).ToArray());
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Removes a loot table; its rules cascade.</summary>
    public async Task DeleteLootTableAsync(
        string templateKey,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            "DELETE FROM public.monster_loot_tables WHERE template_key = @templateKey;");
        command.Parameters.AddWithValue("templateKey", templateKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Bulk drop-rate adjustment. <paramref name="templateKey"/> null means every
    /// loot table in the database; the rate is clamped to the schema range.
    /// </summary>
    public async Task<int> MultiplyChancesAsync(
        double factor,
        string? templateKey = null,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            UPDATE public.monster_loot_rules
               SET chance_basis_points = GREATEST(
                       1,
                       LEAST(10000, ROUND(chance_basis_points * @factor))),
                   updated_at = now()
            """;
        if (templateKey is not null)
        {
            sql += " WHERE template_key = @templateKey";
        }

        await using var command = Source.CreateCommand(sql + ";");
        command.Parameters.AddWithValue("factor", factor);
        if (templateKey is not null)
        {
            command.Parameters.AddWithValue("templateKey", templateKey);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Sets every drop rate (optionally for one table) to one value.</summary>
    public async Task<int> SetChancesAsync(
        int chanceBasisPoints,
        string? templateKey = null,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(chanceBasisPoints, 1, 10000);
        var sql = """
            UPDATE public.monster_loot_rules
               SET chance_basis_points = @chance,
                   updated_at = now()
            """;
        if (templateKey is not null)
        {
            sql += " WHERE template_key = @templateKey";
        }

        await using var command = Source.CreateCommand(sql + ";");
        command.Parameters.AddWithValue("chance", clamped);
        if (templateKey is not null)
        {
            command.Parameters.AddWithValue("templateKey", templateKey);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<ItemRow>> LoadItemsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, COALESCE(name_key, ''), display_name, COALESCE(kind, '')
              FROM public.item_templates
             ORDER BY id;
            """;
        await using var command = Source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ItemRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ItemRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                string.Empty,
                reader.GetString(3)));
        }

        return items;
    }

    /// <summary>
    /// Mirrors the checks in verify_monster_loot_migration.sql plus the runtime
    /// invariant the server enforces on startup.
    /// </summary>
    public async Task<List<LootProblem>> SelfCheckAsync(
        CancellationToken cancellationToken = default)
    {
        var problems = new List<LootProblem>();

        await using (var command = Source.CreateCommand(
            """
            SELECT tb.template_key, tb.maximum_drops, count(rule.loot_index)::int AS rules
              FROM public.monster_loot_tables tb
              LEFT JOIN public.monster_loot_rules rule
                ON rule.template_key = tb.template_key
             GROUP BY tb.template_key, tb.maximum_drops
            HAVING count(rule.loot_index) < tb.maximum_drops
             ORDER BY tb.template_key;
            """))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                problems.Add(new LootProblem(
                    "致命",
                    reader.GetString(0),
                    $"规则数 {reader.GetInt32(2)} 少于最大掉落数 {reader.GetInt16(1)}，" +
                    "服务端启动会失败。"));
            }
        }

        await using (var command = Source.CreateCommand(
            """
            SELECT tb.template_key
              FROM public.monster_loot_tables tb
             WHERE NOT EXISTS (
                   SELECT 1 FROM public.monster_loot_rules rule
                    WHERE rule.template_key = tb.template_key)
             ORDER BY tb.template_key;
            """))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                problems.Add(new LootProblem(
                    "致命",
                    reader.GetString(0),
                    "掉落表没有任何规则，服务端启动会失败（应删除该表）。"));
            }
        }

        await using (var command = Source.CreateCommand(
            "SELECT count(*)::int FROM public.monster_loot_tables WHERE NOT enabled;"))
        {
            var disabled = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
            if (disabled > 0)
            {
                problems.Add(new LootProblem(
                    "提示",
                    string.Empty,
                    $"{disabled} 张掉落表处于停用状态，读取时会被忽略。"));
            }
        }

        await using (var command = Source.CreateCommand(
            "SELECT count(*)::int FROM public.monster_loot_rules WHERE NOT enabled;"))
        {
            var disabledRules =
                (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
            if (disabledRules > 0)
            {
                problems.Add(new LootProblem(
                    "提示",
                    string.Empty,
                    $"{disabledRules} 条规则处于停用状态，掷骰时会跳过。"));
            }
        }

        await using (var command = Source.CreateCommand(
            """
            SELECT count(DISTINCT mt.template_key)::int
              FROM public.monster_templates mt
              LEFT JOIN public.monster_loot_tables lt
                ON lt.template_key = mt.template_key
             WHERE lt.template_key IS NULL;
            """))
        {
            var withoutLoot =
                (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
            problems.Add(new LootProblem(
                "提示",
                string.Empty,
                $"{withoutLoot} 个怪物模板还没有掉落表（未配置不等于出错）。"));
        }

        return problems;
    }

    private static void ValidateLoot(
        string templateKey,
        short maximumDrops,
        IReadOnlyList<LootRuleInput> rules)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
        {
            throw new LootValidationException("未选择怪物。");
        }

        if (maximumDrops is < 1 or > 32)
        {
            throw new LootValidationException("最大掉落数必须在 1 到 32 之间。");
        }

        if (rules.Count == 0)
        {
            throw new LootValidationException(
                "掉落表至少要有一条规则；不想掉落请删除整张表。");
        }

        if (rules.Count < maximumDrops)
        {
            throw new LootValidationException(
                $"规则数 {rules.Count} 少于最大掉落数 {maximumDrops}，" +
                "服务端启动会直接失败。请增加规则或降低最大掉落数。");
        }

        var seen = new HashSet<short>();
        foreach (var rule in rules)
        {
            if (rule.LootIndex is < 0 or > 31)
            {
                throw new LootValidationException(
                    $"规则序号 {rule.LootIndex} 超出 0-31 范围。");
            }

            if (!seen.Add(rule.LootIndex))
            {
                throw new LootValidationException(
                    $"规则序号 {rule.LootIndex} 重复。");
            }

            if (rule.ItemId <= 0)
            {
                throw new LootValidationException(
                    $"规则 {rule.LootIndex} 没有选择物品。");
            }

            if (rule.ChanceBasisPoints is < 1 or > 10000)
            {
                throw new LootValidationException(
                    $"规则 {rule.LootIndex} 的概率必须在 0.01% 到 100% 之间。");
            }

            if (rule.MinimumQuantity is < 1 or > 255 ||
                rule.MaximumQuantity is < 1 or > 255 ||
                rule.MaximumQuantity < rule.MinimumQuantity)
            {
                throw new LootValidationException(
                    $"规则 {rule.LootIndex} 的数量区间不合法" +
                    "（1-255，且最大值不小于最小值）。");
            }
        }
    }
}

/// <summary>A validation failure that should be shown to the operator verbatim.</summary>
internal sealed class LootValidationException : Exception
{
    public LootValidationException(string message)
        : base(message)
    {
    }
}
