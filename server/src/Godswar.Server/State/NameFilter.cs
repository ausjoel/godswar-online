namespace Godswar.Server.State;

/// <summary>
/// F-156 (the admin 2026-11-09): the server-side name word filter, swear words and slurs only. The words live in the
/// embedded <c>State/name-filter.txt</c> (GENERATED; one lower-case word per line, # comments; rebuild after
/// editing). The same file drives the client's Filter.dat rewrite (Development/client-1.4.5/tools/f156_filter_dat.py).
/// A name is blocked when it contains a word in any letter case. Used by pet rename (10242); character and guild
/// names have never had a word filter on this server.
/// </summary>
internal static class NameFilter
{
    internal const string ResourceName = "Godswar.Server.State.name-filter.txt";

    private static readonly Lazy<IReadOnlyList<string>> List = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<string> Words => List.Value;

    public static bool Blocks(string name) =>
        List.Value.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> Parse(string text) =>
        text.Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .Select(static line => line.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> Load()
    {
        using var stream = typeof(NameFilter).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
