using Npgsql;

namespace Godswar.LootTool;

/// <summary><c>quest_reward_slots</c> 的一行：某个任务的某个槽位放什么物品（含这件物品的属性）。</summary>
internal sealed record QuestRewardSlot(
    short SlotIndex,
    int ItemId,
    ItemAttributeValues? Attributes = null);

/// <summary><c>quest_reward_values</c> 的一行：某个任务交付时付多少经验/TP/银币/金币。</summary>
internal sealed record QuestRewardValues(int Experience, int TalentPoints, int Silver, int Gold);

/// <summary>任务列表的一行：这个任务在哪张表里有覆盖。</summary>
internal sealed record QuestRewardRow(int QuestId, int SlotCount, bool HasValues)
{
    public bool IsOverridden => SlotCount > 0 || HasValues;

    public string SlotSummary => SlotCount > 0 ? $"{SlotCount} 个槽位" : "未覆盖";

    public string ValueSummary => HasValues ? "已覆盖" : "未覆盖";

    /// <summary>列表上要一眼看出「这个任务的奖励到底由谁决定」，所以分三种覆盖分别说。</summary>
    public string Status => (SlotCount > 0, HasValues) switch
    {
        (true, true) => "已覆盖（物品 + 数值）",
        (true, false) => "已覆盖（仅物品）",
        (false, true) => "已覆盖（仅数值）",
        _ => "未覆盖（沿用内置）"
    };
}

/// <summary>要写进 <c>quest_reward_slots</c> 的一个槽位。</summary>
/// <param name="Attributes">
/// 这一件奖励物品的品质/等级/附加属性；<c>null</c> 与「12 列全 NULL」等价（= 不配置）。
/// </param>
internal sealed record QuestRewardSlotInput(
    short SlotIndex,
    int ItemId,
    ItemAttributeValues? Attributes = null);

/// <summary>
/// 直连 PostgreSQL 读写 GM 拥有的任务奖励覆盖表
/// （<c>quest_reward_slots</c> / <c>quest_reward_values</c>，服务端迁移
/// <c>20260925_169_quest_reward_overrides</c> 建的）。
/// </summary>
/// <remarks>
/// 和怪物掉落表一样，服务端只在启动时读一次
/// （<c>QuestRewardContentCatalog.Install</c>），所以写库之后必须重启服务器才生效。
/// 两张表都可能不存在（旧库还没跑迁移），因此所有读取都先问一次
/// <see cref="HasQuestRewardSchemaAsync"/>，写入路径也会自己再挡一次。
/// </remarks>
internal sealed class QuestRewardStore : IAsyncDisposable
{
    /// <summary>一个任务能有的奖励槽位数，与服务端 <c>MaximumRewardSlots</c> 一致。</summary>
    public const int MaximumSlots = 8;

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

    /// <summary>缺少表时给出的统一提示，界面和异常都用它。</summary>
    /// <remarks>只点表名不点迁移编号：编号会随服务端整理迁移而变，表名不会。</remarks>
    public static string MissingSchemaMessage(string databaseName) =>
        $"数据库 {databaseName} 缺少任务奖励表（quest_reward_slots / quest_reward_values），" +
        "请先启动一次游戏服务端让它跑数据库迁移，再重试。";

    public async Task<bool> HasQuestRewardSchemaAsync(
        CancellationToken cancellationToken = default) =>
        await TableExistsAsync("public.quest_reward_slots", cancellationToken) &&
        await TableExistsAsync("public.quest_reward_values", cancellationToken);

    /// <summary>
    /// 自测回环用：挑一个本库**真实存在**、且还没有任何覆盖的任务 ID。
    /// 先看角色做过的任务与任务-怪物对照表（这两张表不保证存在，先探一下），
    /// 取不到再依次退回 <paramref name="fallbackCandidates"/>（抓包奖励记录里的新手任务）；
    /// 全被占用就返回 0，调用方跳过而不去动别人的数据。
    /// </summary>
    public async Task<int> FindProbeQuestIdAsync(
        IReadOnlyCollection<int> excluded,
        IReadOnlyList<int> fallbackCandidates,
        CancellationToken cancellationToken = default)
    {
        var known = new SortedSet<int>();
        foreach (var table in (string[])
                 [
                     "public.character_quests",
                     "public.quest_monster_references"
                 ])
        {
            if (!await TableExistsAsync(table, cancellationToken))
            {
                continue;
            }

            await using var command = Source.CreateCommand(
                $"SELECT DISTINCT quest_id FROM {table} WHERE quest_id > 0;");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                known.Add(reader.GetInt32(0));
            }
        }

        foreach (var questId in known.Concat(fallbackCandidates))
        {
            if (!excluded.Contains(questId))
            {
                return questId;
            }
        }

        return 0;
    }

    private async Task<bool> TableExistsAsync(
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = Source.CreateCommand(
            "SELECT to_regclass(@table) IS NOT NULL;");
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    /// <summary>
    /// <c>quest_reward_slots</c> 上那 12 列物品属性在不在
    /// （服务端迁移 20260927_214 加的）。缺一列就整体当没有：界面隐藏「属性…」，
    /// 配过属性的槽位保存时会被拦住。
    /// </summary>
    public async Task<bool> HasItemAttributeColumnsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT column_name
              FROM information_schema.columns
             WHERE table_schema = 'public'
               AND table_name = 'quest_reward_slots';
            """;
        await using var command = Source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return ItemAttributeColumns.HasAll(columns);
    }

    /// <summary>
    /// 两张表里出现过的 quest_id 并集，附带各自的覆盖情况：列表就是从这个并集来的。
    /// </summary>
    public async Task<List<QuestRewardRow>> LoadQuestRowsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH ids AS (
                SELECT quest_id FROM public.quest_reward_slots
                UNION
                SELECT quest_id FROM public.quest_reward_values
            ),
            slot_counts AS (
                SELECT quest_id, count(*)::int AS slot_count
                  FROM public.quest_reward_slots
                 GROUP BY quest_id
            )
            SELECT ids.quest_id,
                   COALESCE(slot_counts.slot_count, 0),
                   (value.quest_id IS NOT NULL)
              FROM ids
              LEFT JOIN slot_counts
                ON slot_counts.quest_id = ids.quest_id
              LEFT JOIN public.quest_reward_values value
                ON value.quest_id = ids.quest_id
             ORDER BY ids.quest_id;
            """;
        await using var command = Source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<QuestRewardRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new QuestRewardRow(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetBoolean(2)));
        }

        return rows;
    }

    public async Task<List<QuestRewardSlot>> LoadSlotsAsync(
        int questId,
        CancellationToken cancellationToken = default)
    {
        // 那 12 列物品属性是服务端迁移 20260927_214 加的；旧库没有时按 NULL 读，
        // 界面隐藏「属性…」按钮，而不是假装它们不存在过。
        var hasAttributes = await HasItemAttributeColumnsAsync(cancellationToken);
        var select = new List<string> { "slot_index", "item_id" };
        select.AddRange(ItemAttributeColumns.Names.Select(
            column => hasAttributes ? column : "NULL::smallint"));
        var sql = $"""
            SELECT {string.Join(", ", select)}
              FROM public.quest_reward_slots
             WHERE quest_id = @questId
             ORDER BY slot_index;
            """;
        await using var command = Source.CreateCommand(sql);
        command.Parameters.AddWithValue("questId", questId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var slots = new List<QuestRewardSlot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            slots.Add(new QuestRewardSlot(
                reader.GetInt16(0),
                reader.GetInt32(1),
                ItemAttributeColumns.Read(reader, 2)));
        }

        return slots;
    }

    /// <summary>该任务的数值覆盖；没有行 = 沿用内置，返回 null。</summary>
    public async Task<QuestRewardValues?> LoadValuesAsync(
        int questId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT experience, talent_points, silver, gold
              FROM public.quest_reward_values
             WHERE quest_id = @questId;
            """;
        await using var command = Source.CreateCommand(sql);
        command.Parameters.AddWithValue("questId", questId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new QuestRewardValues(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3))
            : null;
    }

    /// <summary>
    /// 一个事务里写完两张表：槽位先按 quest_id 全删再插入（「有一行 = 完全由这些行决定」，
    /// 少写的槽位就是空的，所以不能只 upsert），数值按是否有覆盖 upsert 或删除。
    /// </summary>
    public async Task SaveAsync(
        int questId,
        IReadOnlyList<QuestRewardSlotInput> slots,
        bool overrideValues,
        QuestRewardValues values,
        CancellationToken cancellationToken = default)
    {
        Validate(questId, slots, overrideValues, values);
        await EnsureSchemaAsync(cancellationToken);
        await EnsureItemsExistAsync(slots, cancellationToken);

        // 旧库没有那 12 列时：没配过属性就照常写（等于什么都不改），配过就直接拦住
        var hasAttributes = await HasItemAttributeColumnsAsync(cancellationToken);
        if (!hasAttributes &&
            slots.Any(static slot => slot.Attributes is { IsEmpty: false }))
        {
            throw new QuestRewardToolException(
                "数据库的 quest_reward_slots 还没有那 12 列物品属性" +
                "（服务端迁移 20260927_214 未应用），写不了品质/等级/附加属性。" +
                "请先重启一次游戏服务端让它跑迁移，或把这些槽位的属性清空。");
        }

        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = new NpgsqlCommand(
            "DELETE FROM public.quest_reward_slots WHERE quest_id = @questId;",
            connection,
            transaction))
        {
            clear.Parameters.AddWithValue("questId", questId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var slot in slots)
        {
            var insertSql = hasAttributes
                ? $"""
                   INSERT INTO public.quest_reward_slots (
                       quest_id, slot_index, item_id, {string.Join(", ", ItemAttributeColumns.Names)})
                   VALUES (@questId, @slotIndex, @itemId, {string.Join(", ", ItemAttributeColumns.Names.Select(static column => "@" + column))});
                   """
                : """
                   INSERT INTO public.quest_reward_slots (quest_id, slot_index, item_id)
                   VALUES (@questId, @slotIndex, @itemId);
                   """;
            await using var insert = new NpgsqlCommand(
                insertSql,
                connection,
                transaction);
            insert.Parameters.AddWithValue("questId", questId);
            insert.Parameters.AddWithValue("slotIndex", slot.SlotIndex);
            insert.Parameters.AddWithValue("itemId", slot.ItemId);
            if (hasAttributes)
            {
                ItemAttributeColumns.AddParameters(insert, slot.Attributes);
            }

            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        if (overrideValues)
        {
            await using var upsert = new NpgsqlCommand(
                """
                INSERT INTO public.quest_reward_values (
                    quest_id, experience, talent_points, silver, gold)
                VALUES (@questId, @experience, @talentPoints, @silver, @gold)
                ON CONFLICT (quest_id) DO UPDATE
                   SET experience = EXCLUDED.experience,
                       talent_points = EXCLUDED.talent_points,
                       silver = EXCLUDED.silver,
                       gold = EXCLUDED.gold;
                """,
                connection,
                transaction);
            upsert.Parameters.AddWithValue("questId", questId);
            upsert.Parameters.AddWithValue("experience", values.Experience);
            upsert.Parameters.AddWithValue("talentPoints", values.TalentPoints);
            upsert.Parameters.AddWithValue("silver", values.Silver);
            upsert.Parameters.AddWithValue("gold", values.Gold);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            // 取消勾选「覆盖数值」= 删掉这一行，任务交付时又按服务端内置的数值付。
            await using var remove = new NpgsqlCommand(
                "DELETE FROM public.quest_reward_values WHERE quest_id = @questId;",
                connection,
                transaction);
            remove.Parameters.AddWithValue("questId", questId);
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>清空一个任务的全部覆盖：两张表里该任务的行一起删，一个事务。</summary>
    public async Task DeleteOverridesAsync(
        int questId,
        CancellationToken cancellationToken = default)
    {
        if (questId <= 0)
        {
            throw new QuestRewardToolException("任务 ID 必须是大于 0 的整数。");
        }

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteDeleteAsync(
            connection,
            transaction,
            "DELETE FROM public.quest_reward_slots WHERE quest_id = @questId;",
            questId,
            cancellationToken);
        await ExecuteDeleteAsync(
            connection,
            transaction,
            "DELETE FROM public.quest_reward_values WHERE quest_id = @questId;",
            questId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteDeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        int questId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("questId", questId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (!await HasQuestRewardSchemaAsync(cancellationToken))
        {
            throw new QuestRewardToolException(MissingSchemaMessage(DatabaseName));
        }
    }

    /// <summary>
    /// item_id 上有指向 item_templates 的外键，先自己查一遍，好过把 23503 原样丢给 GM。
    /// </summary>
    private async Task EnsureItemsExistAsync(
        IReadOnlyList<QuestRewardSlotInput> slots,
        CancellationToken cancellationToken)
    {
        var ids = slots
            .Select(static slot => slot.ItemId)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await using var command = Source.CreateCommand(
            "SELECT id FROM public.item_templates WHERE id = ANY(@ids);");
        command.Parameters.AddWithValue("ids", ids);
        var known = new HashSet<int>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                known.Add(reader.GetInt32(0));
            }
        }

        var missing = ids
            .Where(id => !known.Contains(id))
            .OrderBy(static id => id)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new QuestRewardToolException(
                $"物品 {string.Join("、", missing)} 不在 item_templates 中，" +
                "写进去会被外键拒绝；请用「浏览…」重新选择。");
        }
    }

    private static void Validate(
        int questId,
        IReadOnlyList<QuestRewardSlotInput> slots,
        bool overrideValues,
        QuestRewardValues values)
    {
        if (questId <= 0)
        {
            throw new QuestRewardToolException("任务 ID 必须是大于 0 的整数。");
        }

        var seen = new HashSet<short>();
        foreach (var slot in slots)
        {
            if (slot.SlotIndex is < 0 or >= MaximumSlots)
            {
                throw new QuestRewardToolException(
                    $"槽位 {slot.SlotIndex} 超出 0-{MaximumSlots - 1} 范围。");
            }

            if (!seen.Add(slot.SlotIndex))
            {
                throw new QuestRewardToolException($"槽位 {slot.SlotIndex} 重复。");
            }

            if (slot.ItemId <= 0)
            {
                throw new QuestRewardToolException($"槽位 {slot.SlotIndex} 没有选择物品。");
            }
        }

        if (!overrideValues)
        {
            return;
        }

        if (values.Experience < 0 ||
            values.TalentPoints < 0 ||
            values.Silver < 0 ||
            values.Gold < 0)
        {
            throw new QuestRewardToolException("经验 / TP / 银币 / 金币都不能是负数。");
        }
    }
}

/// <summary>可以直接展示给操作者的任务奖励错误（校验、缺表等）。</summary>
internal sealed class QuestRewardToolException : Exception
{
    public QuestRewardToolException(string message)
        : base(message)
    {
    }
}
