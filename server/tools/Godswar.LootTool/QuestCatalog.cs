using System.Globalization;
using System.Text.RegularExpressions;

namespace Godswar.LootTool;

/// <summary>客户端任务表里的一个任务。</summary>
/// <param name="Faction">0 = 斯巴达，1 = 雅典，其它 = 未标注（Quest.xml 里确实有 -1 的）。</param>
/// <param name="Title">
/// 形如 <c>[Lv3] 木头人</c>（来自 <c>Text\Quest\{id}.dat</c> 的 Title 段）；
/// 客户端没有这个任务的文本文件时回落成 <c>Lv{MinLevel} 任务{id}</c>。
/// </param>
internal sealed record QuestInfo(
    int Id,
    int MinLevel,
    int Faction,
    string Title,
    bool TitleFromClient,
    string GiverName,
    string ResponderName)
{
    public string FactionName => Faction switch
    {
        0 => "斯巴达",
        1 => "雅典",
        _ => "未标注"
    };
}

/// <summary>
/// 客户端自己带的**全部**任务目录，用来给「任务奖励」页签当搜索源。
/// </summary>
/// <remarks>
/// 数据全部来自操作者本机的客户端，工具不内置任何任务副本：
/// <list type="bullet">
/// <item><c>Localization\{语言}\Settings\Sys\Quest.xml</c>：每行一条
/// <c>&lt;QuestNNNN ID="…" MinLevel="…" Faction="…"/&gt;</c>（该文件是 **UTF-8 带 BOM**），
/// 提供 ID / 最低等级 / 阵营 / 发布与交付 NPC。</item>
/// <item><c>Localization\{语言}\Text\Quest\{id}.dat</c>：**UTF-16LE 带 BOM**，
/// 标题在 <c>Title</c> 关键字后面的 <c>{…}</c> 里，且自带 <c>[LvN]</c> 前缀。</item>
/// </list>
/// 语言目录默认 <c>zh_cn</c>，先从客户端里探测（zh_cn → en_us → 第一个存在的目录）。
/// 解析失败不抛异常，错误写进 <see cref="Error"/>（含期望路径），界面直接展示。
/// </remarks>
internal sealed class QuestCatalog
{
    /// <summary>属性名="值" 的通用拆法，避免为每个字段写一套正则。</summary>
    private static readonly Regex AttributePattern = new(
        "([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*\"([^\"]*)\"",
        RegexOptions.Compiled);

    private QuestCatalog(
        string clientRoot,
        string language,
        string questXmlPath,
        string questTextDirectory,
        IReadOnlyList<QuestInfo> quests,
        int titleCount,
        string? error)
    {
        ClientRoot = clientRoot;
        Language = language;
        QuestXmlPath = questXmlPath;
        QuestTextDirectory = questTextDirectory;
        Quests = quests;
        TitleCount = titleCount;
        Error = error;
    }

    public string ClientRoot { get; }

    public string Language { get; }

    public string QuestXmlPath { get; }

    public string QuestTextDirectory { get; }

    public IReadOnlyList<QuestInfo> Quests { get; }

    /// <summary>标题真的从 <c>Text\Quest\*.dat</c> 读到的条数，其余是回落标题。</summary>
    public int TitleCount { get; }

    /// <summary>解析失败的原因；成功时为 null。</summary>
    public string? Error { get; }

    /// <summary>客户端根目录里的语言目录：zh_cn → en_us → 第一个存在的（见 ClientTextCatalog）。</summary>
    public static string DetectLanguage(string clientRoot) =>
        ClientTextCatalog.DetectLanguage(clientRoot);

    public static QuestCatalog Load(string clientRoot)
    {
        var root = clientRoot?.Trim() ?? string.Empty;
        var language = DetectLanguage(root);
        var questXmlPath = Path.Combine(
            root,
            "Localization",
            language,
            "Settings",
            "Sys",
            "Quest.xml");
        var questTextDirectory = Path.Combine(
            root,
            "Localization",
            language,
            "Text",
            "Quest");

        if (root.Length == 0 || !Directory.Exists(root))
        {
            return Failed(
                root,
                language,
                questXmlPath,
                questTextDirectory,
                $"客户端目录不存在：{root}（期望 {questXmlPath}）");
        }

        if (!Directory.Exists(Path.Combine(root, "Localization")))
        {
            return Failed(
                root,
                language,
                questXmlPath,
                questTextDirectory,
                $"客户端目录里没有 Localization 文件夹（期望 {questXmlPath}）");
        }

        if (!File.Exists(questXmlPath))
        {
            return Failed(
                root,
                language,
                questXmlPath,
                questTextDirectory,
                $"找不到任务表文件（期望 {questXmlPath}）");
        }

        try
        {
            var quests = ParseQuests(questXmlPath, questTextDirectory);
            var titleCount = quests.Count(static quest => quest.TitleFromClient);
            return new QuestCatalog(
                root,
                language,
                questXmlPath,
                questTextDirectory,
                quests,
                titleCount,
                error: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(
                root,
                language,
                questXmlPath,
                questTextDirectory,
                $"读取任务表失败：{ex.Message}（文件 {questXmlPath}）");
        }
    }

    private static QuestCatalog Failed(
        string clientRoot,
        string language,
        string questXmlPath,
        string questTextDirectory,
        string error) =>
        new(clientRoot, language, questXmlPath, questTextDirectory, [], 0, error);

    private static List<QuestInfo> ParseQuests(
        string questXmlPath,
        string questTextDirectory)
    {
        var quests = new List<QuestInfo>();
        var seen = new HashSet<int>();
        foreach (var line in ClientTextCatalog.ReadLines(questXmlPath))
        {
            if (!line.Contains("<Quest", StringComparison.Ordinal))
            {
                continue;
            }

            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match match in AttributePattern.Matches(line))
            {
                attributes[match.Groups[1].Value] = match.Groups[2].Value;
            }

            if (!attributes.TryGetValue("ID", out var idText) ||
                !int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                id <= 0 ||
                !seen.Add(id))
            {
                continue;
            }

            var minLevel = ReadInt(attributes, "MinLevel");
            var faction = attributes.ContainsKey("Faction")
                ? ReadInt(attributes, "Faction")
                : -1;
            var title = ReadTitle(questTextDirectory, id);
            quests.Add(new QuestInfo(
                id,
                minLevel,
                faction,
                title ?? $"Lv{minLevel} 任务{id}",
                title is not null,
                ReadText(attributes, "GiverName"),
                ReadText(attributes, "ResponderName")));
        }

        quests.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return quests;
    }

    /// <summary>
    /// <c>Text\Quest\{id}.dat</c> 的 <c>Title</c> 段：关键字后面第一段 <c>{…}</c>，
    /// 大括号内外都可能带空格，所以两头都 trim；读不到就返回 null（由调用方回落）。
    /// </summary>
    private static string? ReadTitle(string questTextDirectory, int questId)
    {
        var path = Path.Combine(
            questTextDirectory,
            questId.ToString(CultureInfo.InvariantCulture) + ".dat");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var lines = ClientTextCatalog.ReadLines(path).ToArray();
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Trim().Equals("Title", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 标题可能和 { 同行，也可能在下面几行，所以拼一小段再取大括号内容
                var block = string.Join(
                    "\n",
                    lines.Skip(index + 1).Take(8));
                var open = block.IndexOf('{');
                if (open < 0)
                {
                    return null;
                }

                var close = block.IndexOf('}', open + 1);
                var title = (close < 0 ? block[(open + 1)..] : block[(open + 1)..close]).Trim();
                return title.Length > 0 ? title : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单个文件读不了就回落标题，不能让整份任务表失败
        }

        return null;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var text) &&
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static string ReadText(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var text) ? text.Trim() : string.Empty;
}
