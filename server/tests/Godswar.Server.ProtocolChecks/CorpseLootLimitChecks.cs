using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// F-166 (b) (the admin 2026-11-09: "only hand out 8 ... only hand out the best 8 items? filter out overflow of low rarity
// items"): everything rolls; a corpse lists at most 8 drops, the best ones (quality, then rarer chance, then value).
internal static class CorpseLootLimitChecks
{
    public const string CheckName = "F-166 corpse loot capped at 8, keeping the best (low-rarity overflow dropped)";

    private const string Boss = "F166_boss";

    public static Task RunAsync()
    {
        try
        {
            ServerRates.ResetToDefaults();
            CheckTenRolledKeepsBestEight();
            CheckBossTableWithExtras();
            CheckRatesAndGuards();
            CheckMermaidDewWithheldBeforeCap();
            CheckMedusaKeepsBest();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static MonsterLootContentSnapshot BossTable(int rules, int maximumDrops, Func<int, int>? chance = null) =>
        new(
            [new MonsterLootTableRule(Boss, maximumDrops)],
            Enumerable.Range(0, rules)
                .Select(index => new MonsterLootRule(Boss, index, (uint)(5000 + index), chance?.Invoke(index) ?? 10_000, 1, 1))
                .ToArray());

    // 6 common table items, 2 rare table items, the Magic Bell and a season item: 10 rolled, 8 kept,
    // the 2 dropped are the commonest.
    private static void CheckTenRolledKeepsBestEight()
    {
        var content = BossTable(8, 8);
        var death = new Guid(166, 1, 1, 1, 2, 3, 4, 5, 6, 7, 8);
        var table = content.RollLoot(Boss, death);
        Check.Equal(8, table.Count, "the boss table rolls 8");
        // As if 5002 and 5005 came from 0.01% rules (a real 0.01% rule almost never rolls in a check).
        var forced = table.Select(static d => d.ItemId is 5002 or 5005 ? d with { ChanceBasisPoints = 1 } : d).ToArray();
        var merged = CorpseLootLimit.Merge(forced, null, 50, [7001u], []);
        Check.Equal(10, merged.Count, "10 drops rolled (8 table + Magic Bell + season)");

        var kept = CorpseLootLimit.KeepBest(merged, CorpseLootLimit.Maximum(ServerRates.Current), out var dropped);
        Check.Equal(8, kept.Count, "8 kept");
        Check.Equal(2, dropped, "2 dropped");
        var ids = kept.Select(static d => d.ItemId).ToHashSet();
        Check.True(ids.Contains(5002) && ids.Contains(5005), "both rare table items are kept");
        Check.True(ids.Contains(MagicBellLootRule.ItemId), "the 0.5% Magic Bell is kept");
        Check.True(ids.Contains(7001), "the season item (5%) is kept");
        var lost = merged.Where(d => !ids.Contains(d.ItemId)).ToArray();
        Check.True(lost.Length == 2 && lost.All(static d => d.ChanceBasisPoints == 10_000),
            "the 2 dropped are 100% common table items");
        // Value then roll order break the tie among the commons.
        var commons = merged.Where(static d => d.ChanceBasisPoints == 10_000).ToArray();
        var expectedLost = commons
            .Select((d, i) => (d, i))
            .OrderByDescending(static p => CorpseLootLimit.Price(p.d.ItemId))
            .ThenBy(static p => p.i)
            .Skip(commons.Length - 2)
            .Select(static p => p.d.ItemId)
            .OrderBy(static id => id);
        Check.Equal(string.Join(',', expectedLost), string.Join(',', lost.Select(static d => d.ItemId).OrderBy(static id => id)),
            "among equal commons the lowest value (then the later roll) goes");
        Check.True(kept.Select(static d => d.LootIndex).SequenceEqual(
                merged.Where(d => ids.Contains(d.ItemId)).Select(static d => d.LootIndex)),
            "kept drops stay in their original order");
        Check.True(CorpseLootLimit.KeepBest(merged, 8, out _).SequenceEqual(kept), "deterministic for the same roll");

        var entries = kept.Select(static (d, i) => new MonsterLootEntry(i, d.LootIndex, d.ItemId, d.Quantity)).ToArray();
        var packet = Godswar.Server.Packets.PacketBuilder.MonsterLoot(1234, death, entries);
        Check.Equal(8u, BitConverter.ToUInt32(packet, 8), "the 10029 packet lists 8 entries");
    }

    // A boss with 8 table rolls + Cerberus book + Magic Bell + season item => 8 kept, all 3 extras present (they are
    // rarer than the 100% table rolls).
    private static void CheckBossTableWithExtras()
    {
        var content = BossTable(8, 8);
        var death = new Guid(166, 3, 3, 1, 2, 3, 4, 5, 6, 7, 8);
        var book = new RolledMonsterLoot(LelantineFarmLootPolicy.CerberusBookLootIndex, 9999, 1, BoundOnPickup: false);
        var merged = CorpseLootLimit.Merge(content.RollLoot(Boss, death), book, 50, [7001u], []);
        Check.Equal(11, merged.Count, "8 table + 3 extras rolled");
        Check.Equal(merged.Count, merged.Select(static d => d.LootIndex).Distinct().Count(), "loot indexes are unique");
        var kept = CorpseLootLimit.KeepBest(merged, 8, out var dropped);
        Check.True(kept.Count == 8 && dropped == 3, "11 rolled => 8 kept, 3 dropped");
        Check.True(new uint[] { 9999, MagicBellLootRule.ItemId, 7001 }.All(id => kept.Any(d => d.ItemId == id)),
            "the Cerberus book, Magic Bell and season item are all kept");

        // Quality outranks chance: a quality-3 common beats a quality-0 rare.
        var good = new RolledMonsterLoot(0, 5000, 1, Attributes: ItemGrantAttributes.None with { Quality = 3 });
        var rare = new RolledMonsterLoot(1, 5001, 1, ChanceBasisPoints: 1);
        Check.True(CorpseLootLimit.RarityOrder(good, rare) < 0, "higher quality ranks first");
        Check.True(CorpseLootLimit.RarityOrder(rare, new RolledMonsterLoot(2, 5002, 1)) < 0, "rarer chance ranks first");
        Check.Equal(0, CorpseLootLimit.RarityOrder(
                new RolledMonsterLoot(3, 5003, 1, Attributes: ItemGrantAttributes.None),
                new RolledMonsterLoot(4, 5003, 1)),
            "quality 1 (stock rows) and 0 (no attributes) are the same tier");
    }

    private static void CheckRatesAndGuards()
    {
        Check.Equal(8, CorpseLootLimit.Maximum(ServerRates.Current), "drop.corpse_max_items defaults to 8");
        Install((ServerRateKeys.DropCorpseMaximumItems, 16m));
        Check.Equal(8, CorpseLootLimit.Maximum(ServerRates.Current), "drop.corpse_max_items cannot be raised above 8");
        Install((ServerRateKeys.DropCorpseMaximumItems, 5m));
        Check.Equal(5, CorpseLootLimit.Maximum(ServerRates.Current), "drop.corpse_max_items 5 lowers the cap");
        ServerRates.ResetToDefaults();

        var twenty = BossTable(20, 20).RollLoot(Boss, new Guid(166, 2, 2, 1, 2, 3, 4, 5, 6, 7, 8));
        Check.Equal(20, twenty.Count, "the table itself still rolls everything (20)");
        Check.Equal(8, CorpseLootLimit.KeepBest(twenty, 99, out _).Count, "KeepBest never returns more than 8");

        var many = Enumerable.Range(0, 12).Select(static i => new MonsterLootEntry(i, i, 5000u, 1)).ToArray();
        var capped = CorpseLootLimit.Cap(many, 8);
        Check.True(capped.Count == 8 && capped.Select(static e => e.PickupIndex).SequenceEqual(Enumerable.Range(0, 8)),
            "the final guard trims a 12-entry list to 8 with pickup indexes 0-7");
    }

    // F-128 + F-166 follow-up: the Atlantis Mermaid's withheld Morning Dew is removed BEFORE the cap, so 9 rolls with
    // a dew among them still list 8 (the best 8 of what can drop), never 7.
    private static void CheckMermaidDewWithheldBeforeCap()
    {
        const int Atlantis = 205;
        var mermaid = AtlantisPetSpawnPolicy.MermaidTemplateKey;
        var rolled = Enumerable.Range(0, 8)
            .Select(static i => new RolledMonsterLoot(i, (uint)(5000 + i), 1))
            .Append(new RolledMonsterLoot(8, 10_130, 1, ChanceBasisPoints: 1)) // a rare dew would rank first
            .ToArray();
        Check.True(AtlantisPetSpawnPolicy.BlocksMermaidKillDrop(Atlantis, mermaid, 10_130), "item 10130 is a Morning Dew");

        var kept = CorpseLootLimit.KeepBestDroppable(rolled, Atlantis, mermaid, 8, out var withheld, out var dropped);
        Check.Equal(1, withheld, "the Mermaid kill withholds the dew");
        Check.Equal(0, dropped, "the 8 droppable rolls all fit: nothing is cut by the cap");
        Check.Equal(8, kept.Count, "the Mermaid corpse lists 8 drops, not 7");
        Check.True(kept.All(static d => !AtlantisPetSpawnPolicy.IsMermaidDewItem(d.ItemId)), "no dew is listed");

        var ten = rolled.Append(new RolledMonsterLoot(9, 5009, 1, ChanceBasisPoints: 50)).ToArray();
        var keptTen = CorpseLootLimit.KeepBestDroppable(ten, Atlantis, mermaid, 8, out withheld, out dropped);
        Check.True(keptTen.Count == 8 && withheld == 1 && dropped == 1, "10 rolls with a dew => 1 withheld, 1 cut, 8 kept");
        Check.True(keptTen.Any(static d => d.ItemId == 5009), "the rare 0.5% roll is kept over a 100% common");

        var elsewhere = CorpseLootLimit.KeepBestDroppable(rolled, 1, mermaid, 8, out withheld, out dropped);
        Check.True(withheld == 0 && dropped == 1 && elsewhere.Any(static d => d.ItemId == 10_130),
            "outside Atlantis nothing is withheld and the rare roll is kept");
    }

    // F-166 follow-up: a Medusa corpse ranks like the others. Its rolls carry the rule's chance; quality is not part of
    // a Medusa roll (no attributes on Medusa loot rules, no quality in item templates), so all tie as Common.
    private static void CheckMedusaKeepsBest()
    {
        var rolled = Enumerable.Range(0, 9)
            .Select(static i => new MedusaRolledLoot(i, (uint)(5000 + i), 1, i == 0 ? 25 : 10_000))
            .Append(new MedusaRolledLoot(9, 5009, 1, 100))
            .ToArray();
        var ranked = GameSessionRegistry.MedusaRanking(rolled);
        Check.True(ranked.Select(static d => (d.LootIndex, d.ItemId, d.Quantity, d.ChanceBasisPoints))
                .SequenceEqual(rolled.Select(static d => (d.LootIndex, d.ItemId, d.Quantity, d.ChanceBasisPoints))) &&
            ranked.All(static d => d.Attributes.Quality == 0),
            "Medusa rolls keep index, item, quantity and chance (no quality)");
        var kept = CorpseLootLimit.KeepBest(ranked, 8, out var dropped);
        Check.True(kept.Count == 8 && dropped == 2, "10 Medusa rolls => 8 kept, 2 dropped");
        Check.True(kept.Any(static d => d.ItemId == 5000) && kept.Any(static d => d.ItemId == 5009),
            "the 0.25% and 1% Medusa drops are kept");
        Check.True(kept.Select(static d => d.LootIndex).SequenceEqual(kept.Select(static d => d.LootIndex).Order()),
            "kept Medusa drops stay in roll order");
    }

    private static void Install(params (string Key, decimal Value)[] values) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            values.Select(static value => KeyValuePair.Create(value.Key, value.Value)),
            1,
            DateTimeOffset.UtcNow,
            "check",
            "test"));
}
