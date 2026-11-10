using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Rates;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// F-166 (b) (the admin, 2026-11-09: "only hand out 8 ... only hand out the best 8 items? filter out overflow of low rarity
/// items"): one corpse never lists more than <see cref="ClientSlots"/> drops in its 10029 packet. Everything rolls as
/// before (table items, Cerberus book, season and spring drops, Magic Bell, runtime-spawn lines); when more than the
/// cap rolled, <see cref="KeepBest"/> keeps the best ones and drops the commonest overflow.
/// </summary>
/// <remarks>
/// SAFETY, not only layout: the Origin client's 10029 parser never clamps the count. Nine or more entries overflow its
/// 8 x 72-byte stack buffer and the CItem drops[8] array inside the monster object (client RE, 2026-11-09).
/// <para>
/// Ranking (<see cref="RarityOrder"/>), all deterministic for one death event:
/// 1. higher quality first (the rolled item's Quality, the tier the client colours by: ItemColor.xml BaseLevel;
///    0 and 1 count as the same Common tier);
/// 2. lower roll chance first (basis points of the rule or extra that rolled it: rarer = better);
/// 3. higher item value first (the template's <c>Money</c> in ItemBaseAttribute.xml);
/// 4. the original roll order.
/// Kept drops stay in their original order. Identical item ids are not merged: every 10029 entry is its own ground
/// item with its own loot index.
/// </para>
/// <para>
/// Rate <c>drop.corpse_max_items</c> (default 8, 1-8) can lower the cap. It can never raise it above
/// <see cref="ClientSlots"/>: a future 16-slot client needs this constant (and the rate's maximum) raised in code.
/// </para>
/// </remarks>
internal static class CorpseLootLimit
{
    /// <summary>Loot slots the client can hold safely (the 2.51 / Origin client has 8).</summary>
    public const int ClientSlots = 8;

    /// <summary>
    /// LOOT PAGING (F-166 note "a 16 loot window"): the most 10029 pages one corpse may hand out. Rate
    /// <c>drop.corpse_pages</c> (default 1 = no paging, exactly the 8-cap behaviour) can raise it to 2.
    /// </summary>
    public const int MaximumPages = 2;

    /// <summary>The loot index range a corpse may use (0-31, as monster_loot_rules.loot_index).</summary>
    public const int LootIndexCount = 32;

    /// <summary>The Cerberus book's chance (LelantineFarmLootPolicy: the admin's 50%).</summary>
    public const int CerberusBookChanceBasisPoints = 5_000;

    /// <summary>
    /// GENERATED ranking value for season and spring drops (their rules do not hand back a chance): 5%.
    /// Only used to order overflow; it changes no drop chance.
    /// </summary>
    public const int SeasonalChanceBasisPoints = 500;

    private static readonly Lazy<FrozenDictionary<uint, long>> Prices = new(LoadPrices);

    /// <summary>The per-corpse cap: the rate, clamped to 1..<see cref="ClientSlots"/>.</summary>
    public static int Maximum(ServerRatesSnapshot rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var value = rates.Get(ServerRateKeys.DropCorpseMaximumItems);
        return (int)Math.Clamp(value, 1m, ClientSlots);
    }

    /// <summary>
    /// LOOT PAGING: how many 10029 pages one corpse may use: rate <c>drop.corpse_pages</c>, clamped to
    /// 1..<see cref="MaximumPages"/>. 1 = today's single window.
    /// </summary>
    public static int Pages(ServerRatesSnapshot rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var value = rates.Get(ServerRateKeys.DropCorpsePages);
        return (int)Math.Clamp(value, 1m, MaximumPages);
    }

    /// <summary>The item's value (template <c>Money</c>); 0 when unknown.</summary>
    public static long Price(uint itemId) => Prices.Value.TryGetValue(itemId, out var price) ? price : 0;

    /// <summary>
    /// Adds the extras that rolled to the table drops: the Cerberus book keeps its own loot index unless a table drop
    /// holds it; every other extra takes the highest loot index the corpse does not use yet. Nothing is cut here.
    /// </summary>
    public static IReadOnlyList<RolledMonsterLoot> Merge(
        IReadOnlyList<RolledMonsterLoot> table,
        RolledMonsterLoot? cerberusBook,
        int? magicBellChanceBasisPoints,
        IEnumerable<uint> seasonItems,
        IEnumerable<uint> springItems)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(seasonItems);
        ArgumentNullException.ThrowIfNull(springItems);
        var all = new List<RolledMonsterLoot>(table);
        var used = new HashSet<int>(table.Select(static drop => drop.LootIndex));
        if (cerberusBook is { } book && used.Add(book.LootIndex))
        {
            all.Add(book with { ChanceBasisPoints = CerberusBookChanceBasisPoints });
        }

        void AddFree(uint itemId, bool? boundOnPickup, int chance)
        {
            for (var index = LootIndexCount - 1; index >= 0; index--)
            {
                if (used.Add(index))
                {
                    all.Add(new RolledMonsterLoot(index, itemId, 1, boundOnPickup, ChanceBasisPoints: chance));
                    return;
                }
            }
        }

        if (magicBellChanceBasisPoints is { } bell)
        {
            AddFree(MagicBellLootRule.ItemId, null, bell);
        }
        foreach (var item in seasonItems)
        {
            AddFree(item, false, SeasonalChanceBasisPoints);
        }
        foreach (var item in springItems)
        {
            AddFree(item, false, SeasonalChanceBasisPoints);
        }
        return all;
    }

    /// <summary>
    /// Compares two drops by rarity: negative when <paramref name="a"/> is the better one. Quality (higher), then roll
    /// chance (lower), then value (higher). Ties are left to the caller's original order.
    /// </summary>
    public static int RarityOrder(RolledMonsterLoot a, RolledMonsterLoot b)
    {
        // Quality 0 (no attributes) and 1 (stock "Common" rows) are the same tier.
        var quality = Math.Max((short)1, b.Attributes.Quality).CompareTo(Math.Max((short)1, a.Attributes.Quality));
        if (quality != 0)
        {
            return quality;
        }
        var chance = a.ChanceBasisPoints.CompareTo(b.ChanceBasisPoints);
        return chance != 0 ? chance : Price(b.ItemId).CompareTo(Price(a.ItemId));
    }

    /// <summary>
    /// The best <paramref name="maximum"/> drops (at most <see cref="ClientSlots"/>), in their original order;
    /// <paramref name="dropped"/> = how many low-rarity drops were filtered out.
    /// </summary>
    public static IReadOnlyList<RolledMonsterLoot> KeepBest(
        IReadOnlyList<RolledMonsterLoot> rolled,
        int maximum,
        out int dropped)
    {
        ArgumentNullException.ThrowIfNull(rolled);
        return KeepBestCore(rolled, Math.Clamp(maximum, 0, ClientSlots), out dropped);
    }

    /// <summary>
    /// LOOT PAGING: the best <paramref name="perPage"/> x <paramref name="pages"/> drops (at most
    /// <see cref="ClientSlots"/> x <see cref="MaximumPages"/> = 16), in their original order. With one page this is
    /// exactly <see cref="KeepBest"/>.
    /// </summary>
    public static IReadOnlyList<RolledMonsterLoot> KeepBestPaged(
        IReadOnlyList<RolledMonsterLoot> rolled,
        int perPage,
        int pages,
        out int dropped)
    {
        ArgumentNullException.ThrowIfNull(rolled);
        pages = Math.Clamp(pages, 1, MaximumPages);
        return pages == 1
            ? KeepBest(rolled, perPage, out dropped)
            : KeepBestCore(rolled, Math.Clamp(perPage, 0, ClientSlots) * pages, out dropped);
    }

    private static IReadOnlyList<RolledMonsterLoot> KeepBestCore(
        IReadOnlyList<RolledMonsterLoot> rolled,
        int maximum,
        out int dropped)
    {
        if (rolled.Count <= maximum)
        {
            dropped = 0;
            return rolled;
        }

        var keep = rolled
            .Select(static (drop, order) => (drop, order))
            .OrderBy(static pair => pair.drop, Comparer<RolledMonsterLoot>.Create(RarityOrder))
            .ThenBy(static pair => pair.order)
            .Take(maximum)
            .Select(static pair => pair.order)
            .ToHashSet();
        dropped = rolled.Count - keep.Count;
        return rolled.Where((_, order) => keep.Contains(order)).ToArray();
    }

    /// <summary>
    /// The corpse's final drops: first removes what this kill may never drop (F-128: the Atlantis Mermaid's kill never
    /// drops a Morning Dew), THEN keeps the best <paramref name="maximum"/>. Withholding first means a withheld drop
    /// can never take one of the 8 slots and leave the corpse short; the result is the best 8 of what can drop.
    /// </summary>
    /// <param name="withheld">How many rolled drops this kill may not drop (removed before ranking).</param>
    /// <param name="dropped">How many droppable low-rarity drops the cap filtered out.</param>
    public static IReadOnlyList<RolledMonsterLoot> KeepBestDroppable(
        IReadOnlyList<RolledMonsterLoot> rolled,
        int mapId,
        string? templateKey,
        int maximum,
        out int withheld,
        out int dropped,
        int pages = 1)
    {
        ArgumentNullException.ThrowIfNull(rolled);
        var droppable = rolled
            .Where(drop => !Godswar.Server.Application.WorldInstances.AtlantisPetSpawnPolicy.BlocksMermaidKillDrop(
                mapId, templateKey, drop.ItemId))
            .ToArray();
        withheld = rolled.Count - droppable.Length;
        return KeepBestPaged(withheld == 0 ? rolled : droppable, maximum, pages, out dropped);
    }

    /// <summary>The last guard before a 10029 list is stored: never more than <see cref="ClientSlots"/> entries.</summary>
    public static IReadOnlyList<MonsterLootEntry> Cap(IReadOnlyList<MonsterLootEntry> entries, int maximum)
    {
        ArgumentNullException.ThrowIfNull(entries);
        maximum = Math.Clamp(maximum, 0, ClientSlots);
        return entries.Count <= maximum
            ? entries
            : entries.Take(maximum).Select(static (entry, index) => entry with { PickupIndex = index }).ToArray();
    }

    /// <summary>
    /// LOOT PAGING: splits a corpse's final list into 10029 pages of at most <paramref name="perPage"/> (never more
    /// than <see cref="ClientSlots"/>) entries, at most <paramref name="pages"/> pages. Every page numbers its pickup
    /// indexes 0..n-1 (the client's 10050 / 10048 index is page-local); the rule loot index, and so the ground key,
    /// stays the stored item's own. With one page the result is exactly <c>[Cap(entries, perPage)]</c>.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<MonsterLootEntry>> Paginate(
        IReadOnlyList<MonsterLootEntry> entries,
        int perPage,
        int pages)
    {
        ArgumentNullException.ThrowIfNull(entries);
        pages = Math.Clamp(pages, 1, MaximumPages);
        if (pages == 1)
        {
            return [Cap(entries, perPage)];
        }

        perPage = Math.Clamp(perPage, 1, ClientSlots);
        var result = new List<IReadOnlyList<MonsterLootEntry>>(pages);
        for (var page = 0; page < pages && page * perPage < entries.Count; page++)
        {
            result.Add(entries
                .Skip(page * perPage)
                .Take(perPage)
                .Select(static (entry, index) => entry with { PickupIndex = index })
                .ToArray());
        }
        return result.Count == 0 ? [Array.Empty<MonsterLootEntry>()] : result;
    }

    private static FrozenDictionary<uint, long> LoadPrices()
    {
        var prices = new Dictionary<uint, long>();
        foreach (var seed in ItemTemplateSeeds.All.Concat(ClientCatalogItemSeeds.All))
        {
            try
            {
                using var json = JsonDocument.Parse(seed.StatsJson);
                if (json.RootElement.TryGetProperty("Money", out var money) &&
                    long.TryParse(money.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price))
                {
                    prices.TryAdd((uint)seed.Id, price);
                }
            }
            catch (JsonException)
            {
                // A seed without readable stats ranks with value 0.
            }
        }
        return prices.ToFrozenDictionary();
    }
}
