using System.Globalization;
using System.Xml;

namespace Godswar.LootTool;

/// <summary>属性表里的一条：附加属性 id 与它的名字。</summary>
/// <param name="MaxLevel">该属性最高能配到几级（优先用服务端表的 max_level）。</param>
/// <param name="Percent">这条属性是百分比还是数值（只影响提示文字）。</param>
internal sealed record ItemAttributeInfo(
    int Id,
    string NameKey,
    string ChineseName,
    short MaxLevel,
    bool Percent)
{
    public string DisplayName => ChineseName.Length > 0
        ? $"{Id} {ChineseName}"
        : $"{Id} {NameKey}";

    /// <summary>下拉里那一行的完整说明。</summary>
    public string LongName => MaxLevel > 0
        ? $"{DisplayName}（最高 {MaxLevel} 级{(Percent ? "，百分比" : string.Empty)}）"
        : DisplayName;
}

/// <summary><c>item_attribute_templates</c> 的一行：服务端认定存在的属性。</summary>
internal sealed record ItemAttributeTemplateRow(
    int Id,
    string NameKey,
    short MaxLevel,
    bool Percent);

/// <summary>
/// 附加属性的「id → 中文名」字典，给「属性…」弹窗的下拉用。
/// </summary>
/// <remarks>
/// 名字不是猜的，两个来源都来自操作者的客户端/数据库：
/// <list type="bullet">
/// <item>哪些属性 id 存在：优先用服务端表 <c>item_attribute_templates</c>
/// （它的 <c>max_level</c> 是服务端真正认的等级上限），表不在时退回客户端
/// <c>Localization\{语言}\Settings\Sys\ItemAppendAttribute.xml</c>（<c>ID</c> + <c>L1..Ln</c>）。</item>
/// <item>中文名：客户端 <c>Localization\{语言}\Text\EquipDescription.dat</c>（**UTF-16LE**，
/// <c>标签&lt;Tab&gt;中文</c>），用 <c>NameKey</c> 去查，例如
/// <c>AttackA → 提升物理攻击力I</c>。</item>
/// </list>
/// 都找不到时 <see cref="Attributes"/> 为空、<see cref="Error"/> 给出期望路径，
/// 弹窗改用纯数字输入（功能不因此卡住）。
/// </remarks>
internal sealed class ItemAttributeCatalog
{
    /// <summary>名字表整个找不到时的空目录，弹窗据此退化。</summary>
    public static ItemAttributeCatalog Empty { get; } = new(
        [],
        "还没有读到属性表。",
        string.Empty,
        string.Empty,
        0,
        usedServerTable: false);

    private readonly Dictionary<int, ItemAttributeInfo> _byId;

    private ItemAttributeCatalog(
        IReadOnlyList<ItemAttributeInfo> attributes,
        string? error,
        string clientTextPath,
        string clientXmlPath,
        int chineseNameCount,
        bool usedServerTable)
    {
        Attributes = attributes;
        Error = error;
        ClientTextPath = clientTextPath;
        ClientXmlPath = clientXmlPath;
        ChineseNameCount = chineseNameCount;
        UsedServerTable = usedServerTable;
        _byId = attributes.ToDictionary(static attribute => attribute.Id);
    }

    public IReadOnlyList<ItemAttributeInfo> Attributes { get; }

    public string? Error { get; }

    /// <summary>中文名出处（EquipDescription.dat 的完整路径）。</summary>
    public string ClientTextPath { get; }

    /// <summary>属性清单出处（ItemAppendAttribute.xml 的完整路径）。</summary>
    public string ClientXmlPath { get; }

    public int ChineseNameCount { get; }

    /// <summary>属性清单是来自库里的 item_attribute_templates 还是客户端 XML。</summary>
    public bool UsedServerTable { get; }

    public ItemAttributeInfo? Find(int id) =>
        _byId.TryGetValue(id, out var attribute) ? attribute : null;

    /// <summary>
    /// 组合两个来源：属性清单（库优先，客户端补齐） + 中文名（客户端文本）。
    /// </summary>
    public static ItemAttributeCatalog Load(
        string? clientRoot,
        IReadOnlyList<ItemAttributeTemplateRow> serverRows)
    {
        var root = clientRoot?.Trim() ?? string.Empty;
        var language = ClientTextCatalog.DetectLanguage(root);
        var textPath = Path.Combine(
            root,
            "Localization",
            language,
            "Text",
            "EquipDescription.dat");
        var xmlPath = Path.Combine(
            root,
            "Localization",
            language,
            "Settings",
            "Sys",
            "ItemAppendAttribute.xml");

        var chinese = ReadChineseNames(textPath);
        var clientRows = File.Exists(xmlPath) ? ReadClientAttributes(xmlPath) : [];

        // 库里的清单优先（它带着服务端认的 max_level），客户端里有而库里没有的补进来
        var rows = new List<ItemAttributeTemplateRow>(serverRows);
        var known = serverRows.Select(static row => row.Id).ToHashSet();
        rows.AddRange(clientRows.Where(row => !known.Contains(row.Id)));
        rows.Sort(static (left, right) => left.Id.CompareTo(right.Id));

        var attributes = rows
            .Select(row => new ItemAttributeInfo(
                row.Id,
                row.NameKey,
                chinese.TryGetValue(row.NameKey, out var name) ? name : string.Empty,
                row.MaxLevel,
                row.Percent))
            .ToList();
        var named = attributes.Count(static attribute => attribute.ChineseName.Length > 0);

        if (attributes.Count == 0)
        {
            return new ItemAttributeCatalog(
                attributes,
                "没找到附加属性表：库里没有 item_attribute_templates，客户端也读不到 " +
                $"{xmlPath}；属性只能填数字 id。",
                textPath,
                xmlPath,
                named,
                serverRows.Count > 0);
        }

        var error = named == 0
            ? $"没找到属性的中文名表（期望 {textPath}），下拉里只显示英文标签。"
            : null;
        return new ItemAttributeCatalog(
            attributes,
            error,
            textPath,
            xmlPath,
            named,
            serverRows.Count > 0);
    }

    /// <summary><c>标签&lt;Tab&gt;文本</c> 一行一条；读不到就是空字典（不影响功能）。</summary>
    private static Dictionary<string, string> ReadChineseNames(string path)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return names;
        }

        try
        {
            foreach (var line in ClientTextCatalog.ReadLines(path))
            {
                var separator = line.IndexOf('\t');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0)
                {
                    names[key] = value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 名字读不到只是显示英文标签，不该让整个弹窗打不开
        }

        return names;
    }

    /// <summary>客户端 XML：元素名就是 NameKey，<c>ID</c> 是属性 id，<c>L1..Ln</c> 的个数就是等级上限。</summary>
    private static List<ItemAttributeTemplateRow> ReadClientAttributes(string path)
    {
        var rows = new List<ItemAttributeTemplateRow>();
        try
        {
            var document = new XmlDocument();
            document.Load(path);
            if (document.DocumentElement is not { } root)
            {
                return rows;
            }

            foreach (XmlNode node in root.ChildNodes)
            {
                if (node is not XmlElement element ||
                    element.GetAttribute("ID") is not { Length: > 0 } idText ||
                    !int.TryParse(
                        idText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var id) ||
                    // 注意：属性 id 从 0 开始（0 = AttackA），0 是合法属性，不是「不配置」
                    id < 0)
                {
                    continue;
                }

                short maxLevel = 0;
                foreach (XmlAttribute attribute in element.Attributes)
                {
                    if (attribute.Name.Length > 1 &&
                        attribute.Name[0] == 'L' &&
                        short.TryParse(
                            attribute.Name[1..],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var level) &&
                        level > maxLevel)
                    {
                        maxLevel = level;
                    }
                }

                rows.Add(new ItemAttributeTemplateRow(
                    id,
                    element.Name,
                    maxLevel,
                    element.GetAttribute("Flag") == "1"));
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // XML 坏了就当客户端没有这份清单，交给库里的表
        }

        return rows;
    }
}
