using System.Buffers.Binary;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// LOOT PAGING (F-166 note: "if a 16 loot window could be created, that'd be great"): rate drop.corpse_pages
// (default 1 = off) lets a corpse keep up to 16 drops and hand them out 8 per 10029 page.
internal static class CorpseLootPagingChecks
{
    public const string CheckName = "LOOT PAGING drop.corpse_pages: off = today's 10029, 2 = a second page of <= 8";

    private const int Header = 12;
    private const int Record = 72;

    public static Task RunAsync()
    {
        try
        {
            ServerRates.ResetToDefaults();
            CheckRateDefinition();
            CheckRateOffIsUnchanged();
            CheckTwelveDropsTwoPages();
            CheckNeverMoreThanEightRecords();
            CheckMermaidAndCapWithPages();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void CheckRateDefinition()
    {
        var definition = ServerRateCatalog.Get(ServerRateKeys.DropCorpsePages);
        Check.True(definition.Key == "drop.corpse_pages" && definition.Kind == ServerRateKind.Absolute &&
                   definition.DefaultValue == 1m && definition.MinimumValue == 1m && definition.MaximumValue == 2m,
            "drop.corpse_pages is an absolute rate, default 1, range 1-2");
        Check.Equal(1, CorpseLootLimit.Pages(ServerRates.Current), "pages default to 1 (off)");
        Install((ServerRateKeys.DropCorpsePages, 5m));
        Check.Equal(2, CorpseLootLimit.Pages(ServerRates.Current), "pages are clamped to 2");
        Install((ServerRateKeys.DropCorpsePages, 0m));
        Check.Equal(1, CorpseLootLimit.Pages(ServerRates.Current), "pages are clamped to at least 1");
        ServerRates.ResetToDefaults();
    }

    // Rate off: the paged pipeline produces exactly the old Cap(KeepBestDroppable(..)) list and the same 10029 bytes.
    private static void CheckRateOffIsUnchanged()
    {
        var death = new Guid(166, 16, 1, 1, 2, 3, 4, 5, 6, 7, 8);
        foreach (var count in new[] { 3, 8, 12, 20 })
        {
            var merged = Rolled(count);
            var oldKept = CorpseLootLimit.KeepBestDroppable(merged, 1, "x", 8, out _, out var oldDropped);
            var oldEntries = CorpseLootLimit.Cap(Entries(oldKept), 8);
            var oldPacket = PacketBuilder.MonsterLoot(1234, death, oldEntries);

            var rates = ServerRates.Current;
            var maximum = CorpseLootLimit.Maximum(rates);
            var pages = CorpseLootLimit.Pages(rates);
            var newKept = CorpseLootLimit.KeepBestDroppable(merged, 1, "x", maximum, out _, out var newDropped, pages);
            var paged = CorpseLootLimit.Paginate(Entries(newKept), maximum, pages);
            Check.True(paged.Count == 1 && newDropped == oldDropped,
                $"rate off, {count} rolled: one page, same overflow count");
            Check.True(PacketBuilder.MonsterLoot(1234, death, paged[0]).SequenceEqual(oldPacket),
                $"rate off, {count} rolled: the 10029 is byte-for-byte today's");
            var queue = new CorpseLootPages(paged);
            foreach (var index in queue.Current.Keys.ToArray())
            {
                queue.Current.Remove(index);
            }
            Check.True(queue.TryAdvance() is null, $"rate off, {count} rolled: no second page after the last pickup");
        }
    }

    // Rate 2, 12 drops: the first 10029 lists 8 (indexes 0-7), after its 8 pickups the second lists 4 with page-local
    // indexes 0-3, ground keys unique, and a page-2 pickup index names the right stored item.
    private static void CheckTwelveDropsTwoPages()
    {
        Install((ServerRateKeys.DropCorpsePages, 2m));
        var rates = ServerRates.Current;
        var maximum = CorpseLootLimit.Maximum(rates);
        var pageCount = CorpseLootLimit.Pages(rates);
        Check.True(maximum == 8 && pageCount == 2, "rate 2 with the default cap: 2 pages of 8");

        var death = new Guid(166, 16, 2, 1, 2, 3, 4, 5, 6, 7, 8);
        var merged = Rolled(12);
        var kept = CorpseLootLimit.KeepBestDroppable(merged, 1, "x", maximum, out _, out var dropped, pageCount);
        Check.True(kept.Count == 12 && dropped == 0, "12 rolled, all 12 kept (16 allowed)");
        var pages = CorpseLootLimit.Paginate(Entries(kept), maximum, pageCount);
        Check.True(pages.Count == 2 && pages[0].Count == 8 && pages[1].Count == 4, "pages of 8 and 4");

        var first = PacketBuilder.MonsterLoot(1234, death, pages[0]);
        Check.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(8)), "the first 10029 lists 8");
        Check.True(pages[0].Select(static e => e.PickupIndex).SequenceEqual(Enumerable.Range(0, 8)),
            "page 1 pickup indexes 0-7");
        Check.True(pages[1].Select(static e => e.PickupIndex).SequenceEqual(Enumerable.Range(0, 4)),
            "page 2 pickup indexes are page-local 0-3");
        var allRules = pages.SelectMany(static p => p).Select(static e => e.RuleLootIndex).ToArray();
        Check.True(allRules.Distinct().Count() == 12 &&
                   allRules.Select(r => MonsterLootGroundKey.Resolve(death, r)).Distinct().Count() == 12,
            "every stored drop keeps its own rule loot index and ground key across pages");

        var queue = new CorpseLootPages(pages);
        Check.True(queue.PageNumber == 1 && queue.Remaining == 4, "page 1 current, 4 waiting");
        for (var pick = 0; pick < 8; pick++)
        {
            Check.True(queue.TryAdvance() is null, $"no page 2 while page 1 still holds {8 - pick}");
            Check.True(queue.Current.Remove(pick), $"page 1 index {pick} picked");
        }
        var next = queue.TryAdvance();
        Check.True(next is { Count: 4 } && queue.PageNumber == 2 && queue.Remaining == 0,
            "after the 8th pickup page 2 becomes current");
        var second = PacketBuilder.MonsterLoot(1234, death, next!);
        Check.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(8)), "the second 10029 lists 4");
        Check.Equal(Header + 4 * Record, second.Length, "the second 10029 is 4 records long");
        for (var index = 0; index < 4; index++)
        {
            var expected = kept[8 + index];
            var stored = queue.Current[index];
            var key = MonsterLootGroundKey.Resolve(death, expected.LootIndex);
            var record = second.AsSpan(Header + index * Record, Record);
            Check.True(stored.ItemId == expected.ItemId && stored.RuleLootIndex == expected.LootIndex &&
                       BinaryPrimitives.ReadUInt32LittleEndian(record) == expected.ItemId &&
                       BinaryPrimitives.ReadUInt32LittleEndian(record[64..]) == key.High &&
                       BinaryPrimitives.ReadUInt32LittleEndian(record[68..]) == key.Low,
                $"page 2 index {index} maps to the 9th+{index} kept drop (item, rule index, ground key)");
        }
        Check.True(!queue.Current.ContainsKey(4), "page 2 has no index 4");
        foreach (var index in Enumerable.Range(0, 4))
        {
            queue.Current.Remove(index);
        }
        Check.True(queue.TryAdvance() is null, "after page 2 the corpse is empty");
        ServerRates.ResetToDefaults();
    }

    private static void CheckNeverMoreThanEightRecords()
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.MonsterLoot(1234, Guid.Empty,
                Enumerable.Range(0, 9).Select(static i => new MonsterLootEntry(i, i, 5000u, 1)).ToArray()),
            "the 10029 builder refuses a ninth record, whatever the rates");
        Check.Equal(Header + 8 * Record,
            PacketBuilder.MonsterLoot(1234, Guid.Empty,
                Enumerable.Range(0, 8).Select(static i => new MonsterLootEntry(i, i, 5000u, 1)).ToArray()).Length,
            "8 records still build");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new CorpseLootPages([Enumerable.Range(0, 9).Select(static i => new MonsterLootEntry(i, i, 5000u, 1)).ToArray()]),
            "a page of 9 is refused");

        var twenty = Rolled(20);
        var kept = CorpseLootLimit.KeepBestPaged(twenty, 99, 99, out var dropped);
        Check.True(kept.Count == 16 && dropped == 4, "paged KeepBest never keeps more than 16");
        var pages = CorpseLootLimit.Paginate(Entries(kept), 99, 99);
        Check.True(pages.Count == 2 && pages.All(static p => p.Count == 8), "16 kept => 2 pages of 8");
        var tooMany = CorpseLootLimit.Paginate(Entries(twenty), 8, 2);
        Check.True(tooMany.Count == 2 && tooMany.Sum(static p => p.Count) == 16,
            "Paginate itself never hands out more than 2 pages of 8");
        Check.Equal(8, CorpseLootLimit.KeepBest(twenty, 99, out _).Count, "unpaged KeepBest still stops at 8");
    }

    // The Mermaid dew is withheld before ranking with pages too; a lower drop.corpse_max_items sets the page size.
    private static void CheckMermaidAndCapWithPages()
    {
        const int Atlantis = 205;
        var mermaid = AtlantisPetSpawnPolicy.MermaidTemplateKey;
        var rolled = Rolled(11).Append(new RolledMonsterLoot(11, 10_130, 1, ChanceBasisPoints: 1)).ToArray();
        var kept = CorpseLootLimit.KeepBestDroppable(rolled, Atlantis, mermaid, 8, out var withheld, out var dropped, 2);
        Check.True(withheld == 1 && dropped == 0 && kept.Count == 11 &&
                   kept.All(static d => !AtlantisPetSpawnPolicy.IsMermaidDewItem(d.ItemId)),
            "rate 2: the Mermaid dew is withheld, the other 11 all kept");

        var small = CorpseLootLimit.Paginate(Entries(CorpseLootLimit.KeepBestPaged(Rolled(12), 5, 2, out var cut)), 5, 2);
        Check.True(cut == 2 && small.Count == 2 && small[0].Count == 5 && small[1].Count == 5,
            "drop.corpse_max_items 5 with 2 pages: 10 kept, pages of 5");
    }

    // count drops with unique loot indexes; every third one rarer so ranking has something to do.
    private static RolledMonsterLoot[] Rolled(int count) =>
        Enumerable.Range(0, count)
            .Select(static i => new RolledMonsterLoot(i, (uint)(5000 + i), 1, ChanceBasisPoints: i % 3 == 0 ? 50 : 10_000))
            .ToArray();

    private static MonsterLootEntry[] Entries(IReadOnlyList<RolledMonsterLoot> kept) =>
        kept.Select(static (d, i) => new MonsterLootEntry(i, d.LootIndex, d.ItemId, d.Quantity, d.BoundOnPickup, d.Attributes))
            .ToArray();

    private static void Install(params (string Key, decimal Value)[] values) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            values.Select(static value => KeyValuePair.Create(value.Key, value.Value)),
            1,
            DateTimeOffset.UtcNow,
            "check",
            "test"));
}
