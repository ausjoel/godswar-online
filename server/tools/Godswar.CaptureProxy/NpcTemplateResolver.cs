using Npgsql;

/// <summary>
/// 把刷怪包里的 template_key 解析成 NPC 的地图位置 (map_id, scene_key)。
///
/// 历史上 <c>TryParseCityNpcSpawn</c> 把地图写死成 Sparta / Athens 两个主城，
/// 其它地图的 NPC 会被当成怪物丢掉。现在两级解析，覆盖全部地图：
///   1. 权威来源——查 <c>npc_spawn_definitions</c> 拿该模板所属地图；
///   2. 兜底——库里没有该模板时，按 <c>{场景}_{序号}_{外观}</c> 推出场景键，
///      再到启动时缓存的场景表里取地图号。
/// 两级都失败就返回 null，调用方跳过落库并打告警——绝不猜一个地图号。
/// </summary>
sealed class NpcTemplateResolver
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Dictionary<string, CapturedNpcTemplateLocation?> _templateCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, short> _sceneMaps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedTemplates = new(StringComparer.Ordinal);

    private NpcTemplateResolver(NpgsqlDataSource dataSource, Dictionary<string, short> sceneMaps)
    {
        _dataSource = dataSource;
        _sceneMaps = sceneMaps;
    }

    /// <summary>
    /// 创建解析器并一次性载入全部场景键。
    /// <c>npc_spawn_definitions</c> 不存在（全新库）时返回 null，调用方跳过 NPC 记录而不阻塞抓包。
    /// </summary>
    public static async Task<NpcTemplateResolver?> CreateAsync(NpgsqlDataSource dataSource)
    {
        var sceneMaps = new Dictionary<string, short>(StringComparer.Ordinal);

        try
        {
            await using var command = dataSource.CreateCommand(
                "SELECT DISTINCT scene_key, map_id FROM npc_spawn_definitions ORDER BY map_id;");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    // 同名场景以最小地图号为准，保证解析结果稳定。
                    sceneMaps.TryAdd(reader.GetString(0), reader.GetInt16(1));
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            Console.Error.WriteLine("[刷怪] npc_spawn_definitions 不存在，本次运行只记录数据包，不写 NPC 刷怪点");
            return null;
        }

        Console.WriteLine($"刷怪场景： 已载入 {sceneMaps.Count} 个场景，NPC 刷怪点全地图可解析");
        return new NpcTemplateResolver(dataSource, sceneMaps);
    }

    /// <summary>
    /// 判断 template_key 是否符合客户端 NPC 的 <c>{场景}_{序号}_{外观}</c> 命名。
    /// 怪物模板是 A_1 / B_12 这类形式，不会命中。
    /// </summary>
    public static bool IsNpcTemplate(string templateKey)
    {
        return DeriveSceneKey(templateKey) is not null;
    }

    /// <summary>
    /// 解析 template_key 对应的地图位置。返回 null 表示无法确定地图，调用方应跳过落库。
    /// </summary>
    public async Task<CapturedNpcTemplateLocation?> ResolveAsync(string templateKey)
    {
        if (_templateCache.TryGetValue(templateKey, out var cached))
        {
            return cached;
        }

        var location = await ResolveUncachedAsync(templateKey);
        _templateCache[templateKey] = location;
        return location;
    }

    private async Task<CapturedNpcTemplateLocation?> ResolveUncachedAsync(string templateKey)
    {
        var fromDefinitions = await QueryDefinitionsAsync(templateKey);
        if (fromDefinitions is not null)
        {
            return fromDefinitions;
        }

        var sceneKey = DeriveSceneKey(templateKey);
        if (sceneKey is null)
        {
            ReportUnresolved(templateKey, "template_key 不符合 {场景}_{序号}_{外观} 命名");
            return null;
        }

        // 模板不在库里：先用整串场景键，再去掉结尾的 _序号 段重试。
        // 刻意不做前缀/近似匹配——Parnitha_1 与 Parnitha_2、Athens 与 Athens_Newbie
        // 这类同前缀场景一旦猜错，坐标就会落到错误的地图，宁可跳过并告警。
        if (_sceneMaps.TryGetValue(sceneKey, out var mapId) ||
            (TryStripNumericSegment(sceneKey, out var trimmed) && _sceneMaps.TryGetValue(trimmed, out mapId)))
        {
            Console.Error.WriteLine(
                $"[刷怪] {templateKey}：npc_spawn_definitions 无该模板，按场景 {sceneKey} 解析为地图 {mapId}");
            return new CapturedNpcTemplateLocation(mapId, sceneKey);
        }

        ReportUnresolved(templateKey, $"场景 {sceneKey} 及其去掉序号后的形式都不在场景表里");
        return null;
    }

    private async Task<CapturedNpcTemplateLocation?> QueryDefinitionsAsync(string templateKey)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT map_id, scene_key FROM npc_spawn_definitions WHERE template_key = @template_key LIMIT 1;");
        command.Parameters.AddWithValue("template_key", templateKey);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new CapturedNpcTemplateLocation(reader.GetInt16(0), reader.GetString(1));
    }

    /// <summary>
    /// 从 <c>{场景}_{序号}_{外观}</c> 里取出 <c>{场景}</c>。取不出来返回 null。
    /// </summary>
    private static string? DeriveSceneKey(string templateKey)
    {
        var lastUnderscore = templateKey.LastIndexOf('_');
        if (lastUnderscore <= 0)
        {
            return null;
        }

        var previousUnderscore = templateKey.LastIndexOf('_', lastUnderscore - 1);
        if (previousUnderscore <= 0 ||
            !IsNumericSegment(templateKey.AsSpan(previousUnderscore + 1, lastUnderscore - previousUnderscore - 1)))
        {
            return null;
        }

        return templateKey[..previousUnderscore];
    }

    private static bool TryStripNumericSegment(string sceneKey, out string trimmed)
    {
        var lastUnderscore = sceneKey.LastIndexOf('_');
        if (lastUnderscore > 0 && IsNumericSegment(sceneKey.AsSpan(lastUnderscore + 1)))
        {
            trimmed = sceneKey[..lastUnderscore];
            return true;
        }

        trimmed = sceneKey;
        return false;
    }

    private static bool IsNumericSegment(ReadOnlySpan<char> segment)
    {
        return !segment.IsEmpty && !segment.ContainsAnyExcept("0123456789");
    }

    private void ReportUnresolved(string templateKey, string reason)
    {
        if (_reportedTemplates.Add(templateKey))
        {
            Console.Error.WriteLine($"[刷怪] 跳过 {templateKey}：{reason}");
        }
    }
}
