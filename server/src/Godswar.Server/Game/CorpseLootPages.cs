using Godswar.Server.Application.World.Content;

namespace Godswar.Server.Game;

/// <summary>
/// LOOT PAGING (F-166 note: "if a 16 loot window could be created, that'd be great"): the 10029 pages of one corpse.
/// Only the current page can be clicked (10050) or picked up (10048 / 10114 / 10056), keyed by its page-local pickup
/// index 0..7. When the last item of the current page is picked up, <see cref="TryAdvance"/> makes the next page
/// current and the caller sends it as a fresh 10029 for the same corpse (every 10029 makes the client clear and
/// refill that corpse's drop list, client RE 2026-11-09). With one page (rate <c>drop.corpse_pages</c> = 1, the
/// default) there is never a next page and nothing changes. Not thread-safe: the owner locks around it.
/// </summary>
internal sealed class CorpseLootPages
{
    private readonly IReadOnlyList<IReadOnlyList<MonsterLootEntry>> _pages;
    private int _page;

    public CorpseLootPages(IReadOnlyList<IReadOnlyList<MonsterLootEntry>> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0 || pages.Any(static page => page.Count > CorpseLootLimit.ClientSlots))
        {
            throw new ArgumentOutOfRangeException(nameof(pages), "a 10029 page holds at most 8 drops");
        }
        _pages = pages;
        Current = pages[0].ToDictionary(static entry => entry.PickupIndex);
    }

    /// <summary>The page the client shows, by page-local pickup index.</summary>
    public Dictionary<int, MonsterLootEntry> Current { get; private set; }

    /// <summary>1-based number of the current page.</summary>
    public int PageNumber => _page + 1;

    public int PageCount => _pages.Count;

    /// <summary>Drops still waiting on later pages.</summary>
    public int Remaining => _pages.Skip(_page + 1).Sum(static page => page.Count);

    /// <summary>
    /// When the current page is empty and a later page holds drops, makes it current and returns it (the entries
    /// for the next 10029). Otherwise null and nothing changes.
    /// </summary>
    public IReadOnlyList<MonsterLootEntry>? TryAdvance()
    {
        if (Current.Count != 0)
        {
            return null;
        }
        while (_page + 1 < _pages.Count)
        {
            _page++;
            var next = _pages[_page];
            if (next.Count > 0)
            {
                Current = next.ToDictionary(static entry => entry.PickupIndex);
                return next;
            }
        }
        return null;
    }
}
