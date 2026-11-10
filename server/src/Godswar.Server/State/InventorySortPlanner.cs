using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal enum InventorySortTarget : byte
{
    Bag = 1,
    Warehouse = 2
}

/// <summary>
/// The player chat command that asks for a sort: <c>/sort</c> or <c>/sortbag</c> for the bag,
/// <c>/sortwh</c> or <c>/sort wh</c> (also <c>warehouse</c>) for the warehouse.
/// </summary>
internal static class InventorySortCommand
{
    /// <summary>
    /// Parses a chat line as the client sends it: <c>Name:/sort</c> (the speaker prefix is stripped the same way every
    /// other chat command strips it) or a bare <c>/sort</c>.
    /// </summary>
    public static bool TryParseChatLine(string? line, out InventorySortTarget target)
    {
        target = default;
        var offset = Godswar.Server.Game.DeveloperCommandText.FindBodyOffset(line ?? string.Empty);
        return offset >= 0 && TryParse(line![offset..], out target);
    }

    public static bool TryParse(string? text, out InventorySortTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var words = text.Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (words.Length)
        {
            case 1 when words[0] is "/sort" or "/sortbag":
                target = InventorySortTarget.Bag;
                return true;
            case 1 when words[0] is "/sortwh" or "/sortwarehouse":
                target = InventorySortTarget.Warehouse;
                return true;
            case 2 when words[0] == "/sort" && words[1] is "bag":
                target = InventorySortTarget.Bag;
                return true;
            case 2 when words[0] == "/sort" && words[1] is "wh" or "warehouse":
                target = InventorySortTarget.Warehouse;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Plans an in-place sort of a bag or warehouse as a short list of slot swaps.
/// </summary>
/// <remarks>
/// A swap exchanges two WHOLE slot records. Nothing is rebuilt from item ids, so forge levels, sockets, binding,
/// expiry and stack counts travel with the item. Empty slots always end up last. Two slots holding byte-identical
/// records are never swapped (nothing observable would change). At most <c>count - 1</c> swaps are planned and a
/// sorted bag plans none, so running the sort twice is a no-op.
/// </remarks>
internal static class InventorySortPlanner
{
    /// <summary>Exchange the records in two slots. <see cref="Source"/> always holds an item.</summary>
    internal readonly record struct Swap(int Source, int Destination);

    public static IReadOnlyList<Swap> PlanSwaps(
        IReadOnlyList<string> entries,
        Comparison<CompactItemEntry> order)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(order);
        var count = entries.Count;
        var parsed = new CompactItemEntry[count];
        for (var i = 0; i < count; i++)
        {
            parsed[i] = CompactItemEntry.Parse(entries[i]);
        }

        // target[slot] = the original index of the record that belongs in that slot.
        var target = Enumerable.Range(0, count).ToArray();
        Array.Sort(target, (a, b) =>
        {
            var emptyA = parsed[a].IsEmpty;
            var emptyB = parsed[b].IsEmpty;
            if (emptyA != emptyB)
            {
                return emptyA ? 1 : -1;
            }

            if (!emptyA)
            {
                var byOrder = order(parsed[a], parsed[b]);
                if (byOrder != 0)
                {
                    return byOrder;
                }
            }

            return a.CompareTo(b); // total order: equal items keep their relative positions
        });

        var at = Enumerable.Range(0, count).ToArray();    // at[slot]  = original index now in that slot
        var where = Enumerable.Range(0, count).ToArray(); // where[i]  = slot now holding original record i
        var swaps = new List<Swap>();
        for (var slot = 0; slot < count; slot++)
        {
            var wanted = target[slot];
            var holder = at[slot];
            if (holder == wanted)
            {
                continue;
            }

            var from = where[wanted];
            at[slot] = wanted;
            at[from] = holder;
            where[wanted] = slot;
            where[holder] = from;
            var identical = (parsed[wanted].IsEmpty && parsed[holder].IsEmpty) ||
                string.Equals(entries[wanted], entries[holder], StringComparison.Ordinal);
            if (identical)
            {
                continue;
            }

            swaps.Add(parsed[holder].IsEmpty
                ? new Swap(from, slot)
                : new Swap(slot, from));
        }

        return swaps;
    }

    /// <summary>Applies a plan to a copy of the entries (used by tests and to mirror the stored state).</summary>
    public static string[] Apply(IReadOnlyList<string> entries, IEnumerable<Swap> swaps)
    {
        var result = entries.ToArray();
        foreach (var swap in swaps)
        {
            (result[swap.Source], result[swap.Destination]) = (result[swap.Destination], result[swap.Source]);
        }

        return result;
    }
}

/// <summary>
/// The default sort order. Equipment first, by body slot (head, amulet, glove, armour, ... weapon, shield) and
/// highest required level first; then fashion; then mounts and mount gear; then consumables and everything else
/// by item id, so identical items sit together. Within one item id the better quality and grade come first.
/// </summary>
internal static class InventorySortOrder
{
    public static Comparison<CompactItemEntry> Default(IItemTemplateCatalog templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return (left, right) =>
        {
            var a = Rank(templates, left);
            var b = Rank(templates, right);
            var c = a.Group.CompareTo(b.Group);
            if (c != 0) return c;
            c = a.Slot.CompareTo(b.Slot);
            if (c != 0) return c;
            c = a.NegativeLevel.CompareTo(b.NegativeLevel);
            if (c != 0) return c;
            c = left.Id.CompareTo(right.Id);
            if (c != 0) return c;
            c = right.Quality.CompareTo(left.Quality);
            if (c != 0) return c;
            c = right.Grade.CompareTo(left.Grade);
            if (c != 0) return c;
            return right.Stack.CompareTo(left.Stack);
        };
    }

    private static (int Group, int Slot, int NegativeLevel) Rank(IItemTemplateCatalog templates, CompactItemEntry entry)
    {
        if (!templates.TryGet(entry.Id, out var template))
        {
            return (9, 0, 0); // an id the content does not know: last, by id
        }

        var kind = template.Kind ?? string.Empty;
        if (kind.Equals("stylish", StringComparison.OrdinalIgnoreCase))
        {
            return (1, 0, 0);
        }

        if (kind.StartsWith("mount", StringComparison.OrdinalIgnoreCase))
        {
            return (2, 0, 0);
        }

        if (EquipmentSlots.IsEquipmentKind(kind) && EquipmentSlots.IsEquipmentSlot(template.EquipmentSlot))
        {
            return (0, template.EquipmentSlot, -(template.MinLevel ?? 0));
        }

        return (3, 0, 0);
    }
}
