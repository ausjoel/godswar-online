using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class InventorySortChecks
{
    public const string CheckName = "Bag sort plans whole-record swaps, keeps every item and is idempotent";

    public static Task RunAsync()
    {
        CheckCommandParsing();
        CheckPlannerProperties();
        CheckWholeRecordsAndIdenticalItems();
        CheckEqualItemsKeepTheirOrder();
        CheckDefaultOrderOnRealItems();
        return Task.CompletedTask;
    }

    private static void CheckCommandParsing()
    {
        foreach (var (text, expected) in new (string, InventorySortTarget)[]
        {
            ("/sort", InventorySortTarget.Bag), ("/sortbag", InventorySortTarget.Bag), ("/sort bag", InventorySortTarget.Bag),
            ("  /SORT  ", InventorySortTarget.Bag), ("/sortwh", InventorySortTarget.Warehouse),
            ("/sort wh", InventorySortTarget.Warehouse), ("/sort warehouse", InventorySortTarget.Warehouse),
            ("/sortwarehouse", InventorySortTarget.Warehouse)
        })
        {
            Check.True(InventorySortCommand.TryParse(text, out var target) && target == expected, $"'{text}' is the {expected} sort");
        }

        // The client sends "Name:text"; a sort must be recognised behind the speaker prefix and nowhere else.
        foreach (var (line, expected) in new (string, InventorySortTarget)[]
        {
            ("TestChar:/sort", InventorySortTarget.Bag), ("TestChar: /sortwh", InventorySortTarget.Warehouse),
            ("/sort", InventorySortTarget.Bag), ("<TestChar>/sort bag", InventorySortTarget.Bag)
        })
        {
            Check.True(InventorySortCommand.TryParseChatLine(line, out var t) && t == expected, $"chat line '{line}' is the {expected} sort");
        }

        foreach (var line in new[] { "TestChar:sort", "TestChar:hello /sort", "TestChar:/sorted", "Vpst est:/sort", ":/sort", "" })
        {
            Check.True(!InventorySortCommand.TryParseChatLine(line, out _), $"chat line '{line}' stays ordinary chat");
        }

        foreach (var text in new[] { "", "   ", "sort", "/sorted", "/sort all", "/sort bag now", "/sort wh bag", "/sortbags", "hello /sort", "/sor" })
        {
            Check.True(!InventorySortCommand.TryParse(text, out _), $"'{text}' is not a sort command and stays ordinary chat");
        }
    }

    private static void CheckPlannerProperties()
    {
        // Item ids stand in for "type": the order compares id, then better quality, then better grade.
        Comparison<CompactItemEntry> byId = (a, b) =>
        {
            var c = a.Id.CompareTo(b.Id);
            return c != 0 ? c : b.Quality.CompareTo(a.Quality);
        };
        var random = new Random(20261105);
        var pool = new[] { "[]", "[]", "[]", "[4030,,,,,,0,20,1,1,0]", "[4000,,,,,,0,20,1,1,0]", "[4000,,,,,,5,20,1,1,0]", "[1000,1,2,,,,3,10,0,1,0]",
            "[2900,,,,,,1,1,1,1,0]", "[2900,,,,,,1,1,1,1,0]", "[7,,,,,,0,1,0,1,0]" };
        for (var round = 0; round < 300; round++)
        {
            var entries = Enumerable.Range(0, 96).Select(_ => pool[random.Next(pool.Length)]).ToArray();
            var swaps = InventorySortPlanner.PlanSwaps(entries, byId);
            var sorted = InventorySortPlanner.Apply(entries, swaps);

            Check.True(swaps.Count <= entries.Length - 1, "at most count-1 swaps");
            // Replay the plan one swap at a time, as the server does: each swap names two different slots, the source
            // holds an item at that moment, and the two records differ (otherwise nothing would move).
            var replay = entries.ToArray();
            foreach (var swap in swaps)
            {
                Check.True(swap.Source != swap.Destination, "a swap names two different slots");
                Check.True(!CompactItemEntry.Parse(replay[swap.Source]).IsEmpty, "the source of every swap holds an item at that moment");
                Check.True(replay[swap.Source] != replay[swap.Destination], "a swap never exchanges identical records");
                (replay[swap.Source], replay[swap.Destination]) = (replay[swap.Destination], replay[swap.Source]);
            }

            Check.Equal(string.Join("|", sorted), string.Join("|", replay), "Apply and a step-by-step replay agree");
            Check.Equal(string.Join("|", entries.OrderBy(e => e, StringComparer.Ordinal)), string.Join("|", sorted.OrderBy(e => e, StringComparer.Ordinal)),
                "no item is created, lost or altered by a sort");

            var parsed = sorted.Select(CompactItemEntry.Parse).ToArray();
            var firstEmpty = Array.FindIndex(parsed, e => e.IsEmpty);
            Check.True(firstEmpty < 0 || parsed.Skip(firstEmpty).All(e => e.IsEmpty), "empty slots are last");
            var items = parsed.Where(e => !e.IsEmpty).ToArray();
            for (var i = 1; i < items.Length; i++)
            {
                Check.True(byId(items[i - 1], items[i]) <= 0, "items are in order");
            }

            Check.Equal(0, InventorySortPlanner.PlanSwaps(sorted, byId).Count, "sorting a sorted bag plans nothing (idempotent)");
            Check.Equal(string.Join("|", swaps.Select(s => $"{s.Source}>{s.Destination}")),
                string.Join("|", InventorySortPlanner.PlanSwaps(entries, byId).Select(s => $"{s.Source}>{s.Destination}")), "planning is deterministic");
        }
    }

    private static void CheckWholeRecordsAndIdenticalItems()
    {
        // A forged, socketed, bound item: every field after the id must survive byte for byte.
        const string rich = "[1000,13,27,,,,7,19,1,1,5000,0,4,3,,,,2,9,4,8,3]";
        var entries = new[] { "[]", "[4000,,,,,,0,20,1,1,0]", "[]", rich, "[4000,,,,,,0,20,1,1,0]" };
        Comparison<CompactItemEntry> byId = (a, b) => a.Id.CompareTo(b.Id);
        var swaps = InventorySortPlanner.PlanSwaps(entries, byId);
        var sorted = InventorySortPlanner.Apply(entries, swaps);
        Check.Equal(rich, sorted[0], "the forged, socketed item moved to the front with every field intact");
        Check.True(sorted.Contains(rich) && sorted.Count(e => e == "[4000,,,,,,0,20,1,1,0]") == 2, "both identical stacks kept");
        Check.True(swaps.All(swap => entries[swap.Source] != entries[swap.Destination]), "identical records are never swapped");

        // Two empty slots, even if written differently, are never exchanged.
        var empties = new[] { "[]", " ", "[0]", "[4000,,,,,,0,20,1,1,0]" };
        var emptySwaps = InventorySortPlanner.PlanSwaps(empties, byId);
        Check.Equal(1, emptySwaps.Count, "only the item moves; empties are not shuffled among themselves");
        Check.True(!CompactItemEntry.Parse(empties[emptySwaps[0].Source]).IsEmpty, "a swap into an empty slot is oriented from the item");
    }


    private static void CheckEqualItemsKeepTheirOrder()
    {
        // 96 records over three ids, all equal in rank within an id, each made distinct by its Exp field (the slot number).
        // A stable sort keeps the records of one id in slot order; an unstable one would shuffle them.
        Comparison<CompactItemEntry> byId = (a, b) => a.Id.CompareTo(b.Id);
        var ids = new uint[] { 4030, 4000, 2900 };
        var entries = Enumerable.Range(0, 96).Select(slot => $"[{ids[slot * 7 % 3]},,,,,,0,1,0,1,{slot}]").ToArray();
        var sorted = InventorySortPlanner.Apply(entries, InventorySortPlanner.PlanSwaps(entries, byId));
        foreach (var group in sorted.Select(CompactItemEntry.Parse).GroupBy(e => e.Id))
        {
            var exp = group.Select(e => e.Exp).ToArray();
            Check.True(exp.SequenceEqual(exp.OrderBy(x => x)), $"records of item {group.Key} keep their original relative order");
        }

        Check.Equal(0, InventorySortPlanner.PlanSwaps(sorted, byId).Count, "a stable result is already sorted");
    }

    private static void CheckDefaultOrderOnRealItems()
    {
        var catalog = TestItemContent.Catalog;
        var order = InventorySortOrder.Default(catalog);
        uint First(Func<Godswar.Server.Application.Items.ItemTemplateDefinition, bool> where) => catalog.All.Where(where).OrderBy(t => t.Id).First().Id;
        var weapon = First(t => t.Kind == "weapon");
        var armour = First(t => t.Kind == "armor");
        var potion = First(t => t.Kind == "consume item");
        string Entry(uint id, int quality = 0, int grade = 1) => $"[{id},,,,,,{quality},{grade},0,1,0]";

        var entries = new[] { Entry(potion), "[]", Entry(armour), Entry(weapon), Entry(99999999u), Entry(potion, quality: 3) };
        var sorted = InventorySortPlanner.Apply(entries, InventorySortPlanner.PlanSwaps(entries, order));
        var ids = sorted.Select(e => CompactItemEntry.Parse(e)).Where(e => !e.IsEmpty).ToArray();
        var slotOf = (uint id) => catalog.All.First(t => t.Id == id).EquipmentSlot;
        Check.True(slotOf(ids[0].Id) <= slotOf(ids[1].Id), "equipment comes first, by body slot");
        Check.Equal(potion, ids[2].Id, "consumables follow equipment");
        Check.True(ids[2].Quality > ids[3].Quality, "within one item id the better quality comes first");
        Check.Equal(99999999u, ids[4].Id, "an id the content does not know sorts last");
        Check.True(CompactItemEntry.Parse(sorted[^1]).IsEmpty, "empty slots are last");
    }
}
