namespace Godswar.Server.Domain.World.Content;

internal static partial class StarterQuestObjectives
{
    /// <summary>
    /// True when a monster's name is the one this quest target is written as,
    /// whatever shape the plural takes.
    /// </summary>
    /// <remarks>
    /// The quest text, the client's monster table and the world's display names
    /// disagree about plurals often enough that comparing the folded names alone
    /// silently drops kills: quest 531 asks for "Woodland Wolves" while the
    /// monster is "Woodland Wolf", and folding the trailing "s" leaves "wolve"
    /// against "wolf". The comparison therefore walks every shape the target's
    /// plural can stand for and accepts a match or a containment either way.
    /// </remarks>
    public static bool SameName(string? target, string? monsterName)
    {
        var wanted = ShapeSet(target);
        if (wanted.Count == 0)
        {
            return false;
        }

        var actual = Needle(monsterName);
        if (actual.Length == 0)
        {
            return false;
        }

        if (wanted.Contains(actual))
        {
            return true;
        }

        foreach (var shape in wanted)
        {
            if (ContainsWords(shape, actual) ||
                ContainsWords(actual, shape))
            {
                return true;
            }
        }

        // A few names are only written apart in the quest text: the client's own
        // table carries "Young RedDragon" for "Young Red Dragons".
        var joined = actual.Replace(" ", string.Empty);
        foreach (var shape in wanted)
        {
            if (string.Equals(
                    shape.Replace(" ", string.Empty),
                    joined,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when every word of <paramref name="needle"/> appears consecutively in
    /// <paramref name="haystack"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not a character-level comparison: quest 535 says "claim
    /// rewards from the board", and "boa" sits inside "board", so a character
    /// match would credit that kill to a Boa. Whole words keep the legitimate
    /// longer phrases working ("Little Snakes outside the Spartan Starting Area"
    /// still contains "Little Snake") without inventing monsters out of ordinary
    /// prose.
    /// </remarks>
    private static bool ContainsWords(string haystack, string needle)
    {
        var hay = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pin = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pin.Length == 0 || pin.Length > hay.Length)
        {
            return false;
        }

        for (var start = 0; start <= hay.Length - pin.Length; start++)
        {
            var found = true;
            for (var index = 0; index < pin.Length; index++)
            {
                if (!string.Equals(
                        hay[start + index],
                        pin[index],
                        StringComparison.Ordinal))
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every needle a name may be written as, singular and plural alike.
    /// </summary>
    internal static IReadOnlySet<string> ShapeSet(string? value)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return found;
        }

        var words = PlainWords(value);
        if (words.Count == 0)
        {
            return found;
        }

        var head = new List<string>(words.Count);
        for (var index = 0; index < words.Count - 1; index++)
        {
            head.Add(FoldHeadWord(words[index]));
        }

        foreach (var stem in LastWordStems(words[^1]))
        {
            var joined = head.Count == 0
                ? stem
                : string.Join(' ', head) + " " + stem;
            var needle = Needle(joined);
            if (needle.Length > 0)
            {
                found.Add(needle);
            }
        }

        var whole = Needle(value);
        if (whole.Length > 0)
        {
            found.Add(whole);
        }

        return found;
    }

    /// <summary>Plurals no rule reaches, so the pair is named.</summary>
    private static readonly Dictionary<string, string> IrregularPlurals =
        new(StringComparer.Ordinal)
        {
            ["men"] = "man",
            ["oxen"] = "ox",
        };

    /// <summary>A singular guess for a word that is not the target's last one.</summary>
    private static string FoldHeadWord(string word)
    {
        if (IrregularPlurals.TryGetValue(word, out var singular))
        {
            return singular;
        }

        if (word.Length > 3 && word.EndsWith("men", StringComparison.Ordinal))
        {
            // "Spearmen" is the plural of "Spearman", not of "Spearmen".
            return word[..^3] + "man";
        }

        if (word.Length > 3 && word.EndsWith("ies", StringComparison.Ordinal))
        {
            return word[..^3] + "y";
        }

        if (word.Length > 3 && word.EndsWith("ves", StringComparison.Ordinal))
        {
            return word[..^3] + "f";
        }

        if (word.Length > 3 &&
            !word.EndsWith("ss", StringComparison.Ordinal) &&
            word.EndsWith('s'))
        {
            return word[..^1];
        }

        return word;
    }

    /// <summary>The shapes the last word of a name may be written as.</summary>
    private static IEnumerable<string> LastWordStems(string word)
    {
        yield return word;
        if (word.EndsWith('s'))
        {
            yield return word[..^1];
        }

        if (word.EndsWith("es", StringComparison.Ordinal))
        {
            yield return word[..^2];
        }

        if (word.Length > 3 && word.EndsWith("ies", StringComparison.Ordinal))
        {
            yield return word[..^3] + "y";
        }

        if (word.Length > 3 && word.EndsWith("ves", StringComparison.Ordinal))
        {
            yield return word[..^3] + "f";
        }

        if (word == "men" || word == "oxen")
        {
            // "Wild Oxen" is the quest's word for the client's "Wild Ox".
            yield return IrregularPlurals[word];
        }

        if (word.Length > 3 && word.EndsWith("men", StringComparison.Ordinal))
        {
            yield return word[..^3] + "man";
        }
    }

    /// <summary>
    /// A name split into lowercase words, unfinished: the shapes need the plural
    /// the name was written with, not the one <see cref="Needle"/> folds it to.
    /// </summary>
    private static List<string> PlainWords(string value)
    {
        var words = new List<string>();
        var builder = new System.Text.StringBuilder();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (builder.Length > 0)
            {
                words.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            words.Add(builder.ToString());
        }

        return words;
    }
}
