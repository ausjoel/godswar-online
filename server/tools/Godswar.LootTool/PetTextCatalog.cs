namespace Godswar.LootTool;

/// <summary>
/// Chinese pet labels read from the installed client's
/// <c>Localization\zh_cn\Text\Message_Pet.dat</c> (UTF-16LE, tab separated
/// <c>key -&gt; text</c>). Keys used here: <c>Pet&lt;species&gt;_0</c> for the
/// species name, <c>Pet&lt;runtime skill id&gt;</c> for a skill name, and
/// <c>Genius1..5</c> for the five innate talents. A missing client simply leaves
/// the English server names in place.
/// </summary>
internal sealed class PetTextCatalog
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    private PetTextCatalog()
    {
    }

    public int TextCount => _texts.Count;

    public static PetTextCatalog Load(string? clientRoot)
    {
        var catalog = new PetTextCatalog();
        if (string.IsNullOrWhiteSpace(clientRoot) || !Directory.Exists(clientRoot))
        {
            return catalog;
        }

        var path = Path.Combine(
            clientRoot, "Localization", "zh_cn", "Text", "Message_Pet.dat");
        if (!File.Exists(path))
        {
            return catalog;
        }

        try
        {
            foreach (var line in ClientTextCatalog.ReadLines(path))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0)
                {
                    continue;
                }

                var key = line[..tab].Trim().TrimStart('﻿');
                if (key.Length > 0 && !catalog._texts.ContainsKey(key))
                {
                    catalog._texts[key] = line[(tab + 1)..].Trim();
                }
            }
        }
        catch (IOException)
        {
            // An unreadable data file only costs localization.
        }

        return catalog;
    }

    public string SpeciesName(int speciesId, string fallback) =>
        _texts.TryGetValue($"Pet{speciesId}_0", out var chinese) ? chinese : fallback;

    public string SkillName(int runtimeSkillId, string fallback) =>
        _texts.TryGetValue($"Pet{runtimeSkillId}", out var chinese) ? chinese : fallback;

    /// <summary>Talent labels, indexed the way the client numbers its cells.</summary>
    public string TalentName(int bit, string fallback) =>
        _texts.TryGetValue($"Genius{ClientTalentIndex(bit)}", out var chinese)
            ? chinese
            : fallback;

    private static int ClientTalentIndex(int bit) => bit switch
    {
        1 => 1,
        2 => 2,
        4 => 3,
        8 => 4,
        16 => 5,
        _ => 0
    };
}
