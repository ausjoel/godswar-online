using System.Text;

namespace Godswar.LootTool;

/// <summary>
/// Localized names read straight from the installed game client. The server only
/// stores English <c>display_name</c> values, so Chinese names come from
/// <c>Localization\zh_cn\Monster\**\Monster.ini</c> (section name is the monster
/// template key) and <c>Localization\zh_cn\Text\EquipName.dat</c> (tab separated
/// <c>name_key -&gt; text</c>, keyed by <c>item_templates.name_key</c>).
/// A missing client simply disables localization.
/// </summary>
internal sealed class ClientTextCatalog
{
    private readonly Dictionary<string, string> _itemChinese = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _itemEnglish = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _monsterChinese = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _monsterRegion = new(StringComparer.Ordinal);

    private ClientTextCatalog()
    {
    }

    public int ItemNameCount => _itemChinese.Count;

    public int MonsterNameCount => _monsterChinese.Count;

    public bool HasItemNames => _itemChinese.Count > 0;

    public bool HasMonsterNames => _monsterChinese.Count > 0;

    public static ClientTextCatalog Load(string? clientRoot)
    {
        var catalog = new ClientTextCatalog();
        if (string.IsNullOrWhiteSpace(clientRoot) || !Directory.Exists(clientRoot))
        {
            return catalog;
        }

        var localization = Path.Combine(clientRoot, "Localization");
        catalog.ReadItemNames(
            Path.Combine(localization, "zh_cn", "Text", "EquipName.dat"),
            catalog._itemChinese);
        catalog.ReadItemNames(
            Path.Combine(localization, "en_us", "Text", "EquipName.dat"),
            catalog._itemEnglish);
        catalog.ReadMonsterNames(
            Path.Combine(localization, "zh_cn", "Monster"));
        return catalog;
    }

    /// <summary>
    /// 客户端里的语言目录：zh_cn → en_us → 第一个存在的目录（都没有就退回 zh_cn，
    /// 好让报错信息里的期望路径是人能看懂的）。
    /// </summary>
    public static string DetectLanguage(string? clientRoot)
    {
        const string fallback = "zh_cn";
        if (string.IsNullOrWhiteSpace(clientRoot))
        {
            return fallback;
        }

        var localization = Path.Combine(clientRoot, "Localization");
        if (!Directory.Exists(localization))
        {
            return fallback;
        }

        var languages = Directory
            .EnumerateDirectories(localization)
            .Select(static path => Path.GetFileName(path))
            .Where(static name => name.Length > 0)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var preferred in (string[])["zh_cn", "en_us"])
        {
            if (languages.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            {
                return preferred;
            }
        }

        return languages.Count > 0 ? languages[0] : fallback;
    }

    /// <summary>Chinese name when known, otherwise the server's English name.</summary>
    public string ItemName(string? nameKey, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(nameKey))
        {
            if (_itemChinese.TryGetValue(nameKey, out var chinese))
            {
                return chinese;
            }

            if (_itemEnglish.TryGetValue(nameKey, out var english))
            {
                return english;
            }
        }

        return fallback;
    }

    public string MonsterName(string templateKey, string fallback) =>
        _monsterChinese.TryGetValue(templateKey, out var chinese)
            ? chinese
            : fallback;

    /// <summary>Region folder the Chinese definition was read from, e.g. Sparta.</summary>
    public string MonsterRegion(string templateKey) =>
        _monsterRegion.TryGetValue(templateKey, out var region) ? region : string.Empty;

    private void ReadItemNames(string path, Dictionary<string, string> target)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var line in ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) ||
                    line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split('\t', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                var key = parts[0].Trim();
                var value = parts[1].Trim();
                if (key.Length > 0 && value.Length > 0)
                {
                    target[key] = value;
                }
            }
        }
        catch (IOException)
        {
            // Localization is optional; a locked file must not stop the tool.
        }
    }

    private void ReadMonsterNames(string monsterRoot)
    {
        if (!Directory.Exists(monsterRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(
                     monsterRoot,
                     "*.ini",
                     SearchOption.AllDirectories))
        {
            var region = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
            try
            {
                foreach (var line in ReadLines(file))
                {
                    if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
                    {
                        _pendingSection = line[1..^1].Trim();
                        continue;
                    }

                    if (_pendingSection.Length == 0)
                    {
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    if (!line[..separator].Trim().Equals(
                            "Name",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = line[(separator + 1)..].Trim();
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    _monsterChinese.TryAdd(_pendingSection, value);
                    if (region.Length > 0)
                    {
                        _monsterRegion.TryAdd(_pendingSection, region);
                    }
                }
            }
            catch (IOException)
            {
                // Skip an unreadable file and keep the rest of the catalogue.
            }
        }
    }

    private string _pendingSection = string.Empty;

    /// <summary>
    /// The client ships a mix of encodings: UTF-16LE with a byte-order mark for
    /// the per-region <c>Monster.ini</c> files, UTF-8 for the text tables. Detect
    /// the mark first, then fall back to UTF-8 and finally Latin-1 so a stray
    /// file cannot abort the whole scan (ASCII keys survive either way).
    /// </summary>
    internal static IEnumerable<string> ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Latin1.GetString(bytes);
            }
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }
}
