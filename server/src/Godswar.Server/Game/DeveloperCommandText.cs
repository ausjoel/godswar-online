namespace Godswar.Server.Game;

// ROUND2: one rule for every chat command. The stock client prefixes a chat
// line with the speaker's name ("Name:text"; a private line "ToName:text"), so
// a line is a command only when its message - the text itself, or the part
// after that first "Name:" prefix - starts with the command word, followed by
// a space or the end of the line. "selling /items cheap", "/items" and "I am
// /level 5 now" are therefore ordinary chat again.
internal static class DeveloperCommandText
{
    /// <summary>
    /// The offset of the message body (where a command must start), or -1
    /// when the line has no body that starts with '/'.
    /// </summary>
    public static int FindBodyOffset(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return -1;
        }

        var start = SkipWhitespace(text, 0);
        if (start < text.Length && text[start] == '/')
        {
            return start;
        }

        // The name prefix: no whitespace before the first ':' (or the '>' of
        // a bracketed name).
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character))
            {
                return -1;
            }

            if (character is ':' or '>')
            {
                if (index == start)
                {
                    return -1;
                }

                var body = SkipWhitespace(text, index + 1);
                return body < text.Length && text[body] == '/' ? body : -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds which of <paramref name="words"/> the message starts with (the
    /// longest one when several match) and returns its offset, or -1.
    /// </summary>
    public static int FindCommandOffset(
        string text,
        IEnumerable<string> words,
        out string matchedWord)
    {
        matchedWord = string.Empty;
        var body = FindBodyOffset(text);
        if (body < 0)
        {
            return -1;
        }

        foreach (var word in words)
        {
            if (word.Length > matchedWord.Length &&
                StartsWithWord(text, body, word))
            {
                matchedWord = word;
            }
        }

        return matchedWord.Length > 0 ? body : -1;
    }

    public static bool StartsWithWord(string text, int offset, string word) =>
        offset >= 0 &&
        string.Compare(
            text,
            offset,
            word,
            0,
            word.Length,
            StringComparison.OrdinalIgnoreCase) == 0 &&
        offset + word.Length <= text.Length &&
        (offset + word.Length == text.Length ||
            char.IsWhiteSpace(text[offset + word.Length]));

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }
}
