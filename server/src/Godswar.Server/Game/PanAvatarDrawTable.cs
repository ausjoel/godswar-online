using System.Globalization;

namespace Godswar.Server.Game;

/// <summary>
/// PAN CHAIN (the admin F-37): the Avatar of Pan's GENERATED certificate draw, loaded from the embedded
/// <c>Game/pan-avatar-draw.csv</c> (the single editable source; rebuild after changing it).
/// </summary>
internal static class PanAvatarDrawTable
{
    internal const string ResourceName = "Godswar.Server.Game.pan-avatar-draw.csv";
    internal const int MaximumQuantity = 99; // the admin: no stack above 99

    internal readonly record struct Entry(uint ItemId, int Quantity, int Weight, string Note);

    private static readonly Lazy<IReadOnlyList<Entry>> Table = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static IReadOnlyList<Entry> Entries => Table.Value;

    /// <summary>One weighted pick; <paramref name="roll"/> is in [0, total weight).</summary>
    public static Entry Pick(int roll)
    {
        var entries = Table.Value;
        foreach (var entry in entries)
        {
            if (roll < entry.Weight)
            {
                return entry;
            }

            roll -= entry.Weight;
        }

        return entries[^1];
    }

    public static int TotalWeight => Table.Value.Sum(static entry => entry.Weight);

    private static IReadOnlyList<Entry> Load()
    {
        using var stream = typeof(PanAvatarDrawTable).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    internal static IReadOnlyList<Entry> Parse(string text)
    {
        var entries = new List<Entry>();
        var header = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!header)
            {
                if (!line.StartsWith("item_id,quantity,weight", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"{ResourceName}: missing header.");
                }

                header = true;
                continue;
            }

            var cells = line.Split(',', 4);
            if (cells.Length < 3 ||
                !uint.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out var item) || item == 0 ||
                !int.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) ||
                quantity is < 1 or > MaximumQuantity ||
                !int.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out var weight) || weight < 1)
            {
                throw new InvalidDataException($"{ResourceName}: bad row '{line}'.");
            }

            entries.Add(new Entry(item, quantity, weight, cells.Length > 3 ? cells[3].Trim() : string.Empty));
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException($"{ResourceName}: no rows.");
        }

        return entries;
    }
}
