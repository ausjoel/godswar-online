using System.Collections.Frozen;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// EVENT DROPS (q07): one row of public.event_drop_windows. The listed items drop from monster loot tables only
/// while an enabled window that lists them is open. <paramref name="TableKey"/> null = every table; otherwise one
/// loot table key (a plain template key also covers its "template@map" area tables). Yearly windows repeat on the
/// same dates every year.
/// </summary>
internal sealed record EventDropWindow(
    string Name,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool Enabled,
    bool Yearly,
    IReadOnlyCollection<uint> ItemIds,
    string? TableKey)
{
    public bool IsOpenAt(DateTimeOffset at)
    {
        if (!Enabled)
        {
            return false;
        }

        if (at < StartUtc)
        {
            return false;
        }

        if (!Yearly)
        {
            return at < EndUtc;
        }

        // This year's copy of the dates, or last year's for a window that runs across New Year.
        var years = at.UtcDateTime.Year - StartUtc.UtcDateTime.Year;
        for (var shift = Math.Max(0, years - 1); shift <= years; shift++)
        {
            var start = StartUtc.AddYears(shift);
            var end = EndUtc.AddYears(shift);
            if (at >= start && at < end)
            {
                return true;
            }
        }

        return false;
    }

    public bool Covers(string tableKey)
    {
        if (TableKey is null || string.Equals(TableKey, tableKey, StringComparison.Ordinal))
        {
            return true;
        }

        var separator = tableKey.IndexOf(MonsterLootContentSnapshot.AreaKeySeparator);
        return separator > 0 && string.Equals(TableKey, tableKey[..separator], StringComparison.Ordinal);
    }
}

internal sealed class EventDropWindowSnapshot
{
    private readonly EventDropWindow[] _windows;
    private readonly FrozenSet<uint> _named;

    public EventDropWindowSnapshot(IReadOnlyList<EventDropWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        _windows = windows.ToArray();
        _named = _windows.SelectMany(static window => window.ItemIds).ToFrozenSet();
    }

    public static EventDropWindowSnapshot Empty { get; } = new([]);

    public IReadOnlyList<EventDropWindow> Windows => _windows;

    public int GatedItemCount => _named.Count;

    /// <summary>
    /// False only when some window (enabled or not) names this item for this table and none of those windows is
    /// open. An item no window names always drops, so no rows = the stock tables unchanged.
    /// </summary>
    public bool AllowsDrop(string tableKey, uint itemId, DateTimeOffset at)
    {
        if (!_named.Contains(itemId))
        {
            return true;
        }

        var named = false;
        foreach (var window in _windows)
        {
            if (!window.ItemIds.Contains(itemId) || !window.Covers(tableKey))
            {
                continue;
            }

            if (window.IsOpenAt(at))
            {
                return true;
            }

            named = true;
        }

        return !named;
    }
}

internal static class EventDropWindowCatalog
{
    private static EventDropWindowSnapshot _current = EventDropWindowSnapshot.Empty;

    public static EventDropWindowSnapshot Current => Volatile.Read(ref _current);

    public static void Install(EventDropWindowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
