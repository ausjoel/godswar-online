namespace Godswar.LootTool;

/// <summary>
/// One row of the monster picker. Loot is keyed by <c>template_key</c>, but
/// <c>monster_templates</c> holds one row per (source_key, template_key), so the
/// list is aggregated to one row per template key.
/// </summary>
internal sealed record MonsterRow(
    string TemplateKey,
    string ChineseName,
    string EnglishName,
    string Rank,
    string Scenes,
    string Maps,
    bool HasLootTable,
    short MaximumDrops,
    int RuleCount,
    bool Enabled,
    int SpawnCount)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(ChineseName) ? EnglishName : ChineseName;

    public string LootSummary => HasLootTable
        ? $"{RuleCount} 条规则 / 上限 {MaximumDrops}"
        : "未配置";

    public string Status => HasLootTable
        ? (Enabled ? "已配置" : "已配置(停用)")
        : "未配置";

    /// <summary>
    /// Spawn points in the published world content. A template that never
    /// spawns can never be killed, so its loot can never trigger.
    /// </summary>
    public bool IsSpawned => SpawnCount > 0;

    public string SpawnSummary => SpawnCount > 0
        ? SpawnCount.ToString()
        : "0（不会掉落）";
}

/// <summary>Header row of <c>monster_loot_tables</c>.</summary>
internal sealed record LootHeader(short MaximumDrops, bool Enabled);

/// <summary>One row of <c>monster_loot_rules</c>.</summary>
/// <param name="BoundOnPickup">
/// 拾取后是否绑定：<c>true</c> = 绑定到拾取的角色（不可交易），<c>false</c> = 可交易，
/// <c>null</c> = 跟随物品模板自己的 <c>BindType</c>（服务端原本的行为）。
/// </param>
internal sealed record LootRule(
    short LootIndex,
    int ItemId,
    int ChanceBasisPoints,
    short MinimumQuantity,
    short MaximumQuantity,
    bool Enabled,
    bool? BoundOnPickup = null,
    ItemAttributeValues? Attributes = null);

/// <summary>One selectable item, joined with the client's localized name.</summary>
internal sealed record ItemRow(
    int Id,
    string NameKey,
    string DisplayName,
    string ChineseName,
    string Kind)
{
    public string SearchText => $"{Id}\t{NameKey}\t{DisplayName}\t{ChineseName}\t{Kind}";
}

/// <summary>A monetarised rule as edited in the grid.</summary>
/// <param name="BoundOnPickup">
/// 三态必须原样落库：<c>null</c> 不能写成 <c>false</c>（那会把「跟随物品模板」变成「可交易」）。
/// </param>
/// <param name="Attributes">
/// 这一件掉落物品的品质/等级/附加属性；<c>null</c> 与「12 列全 NULL」等价（= 不配置）。
/// </param>
internal sealed record LootRuleInput(
    short LootIndex,
    int ItemId,
    int ChanceBasisPoints,
    short MinimumQuantity,
    short MaximumQuantity,
    bool Enabled,
    bool? BoundOnPickup = null,
    ItemAttributeValues? Attributes = null);

/// <summary>Problems found by the built-in schema/consistency self-check.</summary>
internal sealed record LootProblem(string Severity, string TemplateKey, string Message);

/// <summary>
/// 一件物品实例的属性：品质 / 等级 / 最多 5 条附加属性（属性 id + 该属性的等级）。
/// </summary>
/// <remarks>
/// 12 个值都可以是 NULL，NULL = 不配置，服务端仍按现状发（品质 1 / 等级 1 / 无附加属性）。
/// <para>
/// 这是**这一件物品**的属性，不是物品模板的属性：同一个物品从任务奖励给和从怪掉落
/// 可以配成两套，互不影响（分别存在 quest_reward_slots 与 monster_loot_rules 里）。
/// </para>
/// </remarks>
internal sealed record ItemAttributeValues(
    short? Quality = null,
    short? Grade = null,
    short? Attribute1 = null,
    short? AttributeLevel1 = null,
    short? Attribute2 = null,
    short? AttributeLevel2 = null,
    short? Attribute3 = null,
    short? AttributeLevel3 = null,
    short? Attribute4 = null,
    short? AttributeLevel4 = null,
    short? Attribute5 = null,
    short? AttributeLevel5 = null)
{
    /// <summary>12 列全 NULL（= 不配置）。</summary>
    public bool IsEmpty =>
        Quality is null && Grade is null &&
        Attribute1 is null && AttributeLevel1 is null &&
        Attribute2 is null && AttributeLevel2 is null &&
        Attribute3 is null && AttributeLevel3 is null &&
        Attribute4 is null && AttributeLevel4 is null &&
        Attribute5 is null && AttributeLevel5 is null;

    /// <summary>5 组「属性 id + 等级」，顺序就是 attribute1..5。</summary>
    public IEnumerable<(short? Id, short? Level)> Pairs
    {
        get
        {
            yield return (Attribute1, AttributeLevel1);
            yield return (Attribute2, AttributeLevel2);
            yield return (Attribute3, AttributeLevel3);
            yield return (Attribute4, AttributeLevel4);
            yield return (Attribute5, AttributeLevel5);
        }
    }

    /// <summary>
    /// 表格里那一行的摘要，例如「品质10 等级12 属性:24/5、133/-、90/-」
    /// （属性只配了 id、没配等级时写成 <c>id/-</c>）。
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Quality is { } quality)
            {
                parts.Add($"品质{quality}");
            }

            if (Grade is { } grade)
            {
                parts.Add($"等级{grade}");
            }

            var attributes = new List<string>();
            foreach (var (id, level) in Pairs)
            {
                if (id is { } attribute)
                {
                    attributes.Add(level is { } value ? $"{attribute}/{value}" : $"{attribute}/-");
                }
            }

            if (attributes.Count > 0)
            {
                parts.Add("属性:" + string.Join("、", attributes));
            }

            return parts.Count == 0 ? "（未配置）" : string.Join(" ", parts);
        }
    }
}
