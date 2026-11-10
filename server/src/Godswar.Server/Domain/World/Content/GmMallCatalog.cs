using Godswar.Server.Application.Items;

namespace Godswar.Server.Domain.World.Content;

/// <summary>One tab of a GM Mall window: up to 50 items of one section.</summary>
internal sealed record GmMallTab(string Section, uint[] ItemIds, int MinLevel, int MaxLevel);

/// <summary>One GM Mall window: the client's NPC shop window, four tabs of 50.</summary>
internal sealed record GmMallPage(string Key, string Title, int Number, int Count, GmMallTab[] Tabs)
{
    public bool TryGetItem(int tab, int index, out uint itemId)
    {
        itemId = 0;
        if (tab < 0 || tab >= Tabs.Length || index < 0 || index >= Tabs[tab].ItemIds.Length)
        {
            return false;
        }

        itemId = Tabs[tab].ItemIds[index];
        return true;
    }
}

internal sealed record GmMallCategory(string Key, string Title, string[] Aliases, GmMallPage[] Pages);

/// <summary>
/// The GM Mall (/mall) layout. The item list and its order come from
/// <c>GmMallCatalog.Generated.cs</c> (Development/gm-mall-shop/build_gm_mall_shop.py):
/// only ids the shipped client defines, sorted by level then name inside each
/// section. At start the list is cut to the ids this server's item catalog has.
/// </summary>
/// <remarks>
/// Client limits (Origin.exe 2013): the 10071 handler drops a frame whose category
/// is 4 or more and one with more than 32 records (0x48c148, 0x48c159); NpcTrade.xml
/// draws 50 slots per tab. So a window is four tabs of at most 50, each section
/// starts a new tab, and a category runs over as many windows as it needs.
/// </remarks>
internal static partial class GmMallCatalog
{
    public const int TabSlots = 50;
    public const int TabsPerPage = 4;
    public const string SearchKey = "find";

    internal sealed record GmMallSectionSource(string Title, uint[] Ids);

    internal sealed record GmMallCategorySource(
        string Key, string Title, string[] Aliases, GmMallSectionSource[] Sections);

    public static IReadOnlyList<GmMallCategorySource> Sources => GeneratedCategories;

    public static IReadOnlyList<GmMallCategory> Build(IItemTemplateCatalog templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var categories = new List<GmMallCategory>();
        foreach (var source in GeneratedCategories)
        {
            var tabs = new List<GmMallTab>();
            foreach (var section in source.Sections)
            {
                var ids = section.Ids.Where(id => templates.TryGet(id, out _)).ToArray();
                tabs.AddRange(ids.Chunk(TabSlots).Select(chunk => MakeTab(section.Title, chunk, templates)));
            }

            if (tabs.Count == 0)
            {
                continue;
            }

            categories.Add(new GmMallCategory(
                source.Key, source.Title, source.Aliases, Paginate(source.Key, source.Title, tabs)));
        }

        return categories;
    }

    /// <summary>Exact key or alias first, then a unique key prefix.</summary>
    public static bool TryFind(IReadOnlyList<GmMallCategory> categories, string word, out GmMallCategory category)
    {
        var w = word.Trim().ToLowerInvariant();
        category = categories.FirstOrDefault(c => c.Key == w || c.Aliases.Contains(w))!;
        if (category is not null)
        {
            return true;
        }

        var prefixed = categories.Where(c => w.Length >= 2 && c.Key.StartsWith(w, StringComparison.Ordinal)).ToArray();
        category = prefixed.Length == 1 ? prefixed[0] : null!;
        return category is not null;
    }

    /// <summary>
    /// A one-off window of matches: an item id, or text in the item name. Up to
    /// 200 matches (one window), listed in mall order and split into tabs by
    /// category.
    /// </summary>
    public static GmMallPage? Search(
        IReadOnlyList<GmMallCategory> categories,
        IItemTemplateCatalog templates,
        string query,
        out int total)
    {
        total = 0;
        var q = query.Trim();
        if (q.Length == 0)
        {
            return null;
        }

        var byId = uint.TryParse(q, out var wantedId);
        var seen = new HashSet<uint>();
        var groups = new List<(string Title, List<uint> Ids)>();
        foreach (var category in categories)
        {
            foreach (var id in category.Pages.SelectMany(p => p.Tabs).SelectMany(t => t.ItemIds))
            {
                if (!seen.Add(id) || !templates.TryGet(id, out var template))
                {
                    continue;
                }

                var match = byId
                    ? id == wantedId
                    : template.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!match)
                {
                    continue;
                }

                total++;
                if (groups.Count == 0 || groups[^1].Title != category.Title)
                {
                    groups.Add((category.Title, []));
                }

                groups[^1].Ids.Add(id);
            }
        }

        if (total == 0)
        {
            return null;
        }

        var tabs = new List<GmMallTab>();
        foreach (var (title, ids) in groups)
        {
            tabs.AddRange(ids.Chunk(TabSlots).Select(chunk => MakeTab(title, chunk, templates)));
        }

        // When many categories match, pack them into the four tabs in order.
        if (tabs.Count > TabsPerPage)
        {
            var flat = groups.SelectMany(g => g.Ids).Take(TabSlots * TabsPerPage).ToArray();
            tabs = flat.Chunk(TabSlots).Select(chunk => MakeTab("matches", chunk, templates)).ToList();
        }

        return new GmMallPage(SearchKey, $"Search \"{q}\"", 1, 1, [.. tabs.Take(TabsPerPage)]);
    }

    private static GmMallPage[] Paginate(string key, string title, List<GmMallTab> tabs)
    {
        var chunks = tabs.Chunk(TabsPerPage).ToArray();
        return chunks
            .Select((chunk, index) => new GmMallPage(key, title, index + 1, chunks.Length, chunk))
            .ToArray();
    }

    private static GmMallTab MakeTab(string section, uint[] ids, IItemTemplateCatalog templates)
    {
        var levels = ids
            .Select(id => templates.TryGet(id, out var t) ? t.MinLevel ?? 0 : 0)
            .ToArray();
        return new GmMallTab(section, ids, levels.Min(), levels.Max());
    }
}
