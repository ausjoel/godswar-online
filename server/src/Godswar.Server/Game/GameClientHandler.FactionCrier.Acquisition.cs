using Godswar.Server.Application.FactionCrier;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    internal static IReadOnlyList<CompactItemEntry>
        GetFactionCrierNameplateAcquisitions(
            string bagBefore,
            string bagAfter)
    {
        var before = CountFactionCrierNameplates(bagBefore, null);
        var representatives = new Dictionary<
            (uint ItemId, short Bound), CompactItemEntry>();
        var after = CountFactionCrierNameplates(
            bagAfter,
            representatives);
        var acquisitions = new List<CompactItemEntry>();

        foreach (var pair in after.OrderBy(static pair => pair.Key.ItemId)
                     .ThenBy(static pair => pair.Key.Bound))
        {
            before.TryGetValue(pair.Key, out var previousQuantity);
            var added = pair.Value - previousQuantity;
            while (added > 0)
            {
                // Origin renders this field as a signed byte. Split a larger
                // positive delta so the left-log quantity can never wrap.
                var quantity = Math.Min(added, sbyte.MaxValue);
                acquisitions.Add(representatives[pair.Key] with
                {
                    Stack = checked((short)quantity)
                });
                added -= quantity;
            }
        }

        return acquisitions;
    }

    internal static int GetFactionCrierAcquisitionScratchSlot(
        string bagBefore,
        string bagAfter)
    {
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            var previous = KitBagSlots.GetItem(bagBefore, slot);
            if (previous.IsEmpty ||
                previous != KitBagSlots.GetItem(bagAfter, slot))
            {
                // Changed occupied slots are evicted before MSG_SYS_ADD_ITEM.
                // Origin puts an ordinary SkillFlag-0 Nameplate in the first
                // empty slot, so this is its deterministic transient target.
                return slot;
            }
        }

        return -1;
    }

    private static Dictionary<(uint ItemId, short Bound), int>
        CountFactionCrierNameplates(
            string kitBag,
            Dictionary<(uint ItemId, short Bound), CompactItemEntry>?
                representatives)
    {
        var counts = new Dictionary<(uint ItemId, short Bound), int>();
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            var item = KitBagSlots.GetItem(kitBag, slot);
            if (item.Id is <
                    FactionCrierRewardPolicy.FirstNameplateItemId or >
                    FactionCrierRewardPolicy.LastNameplateItemId ||
                item.Stack <= 0)
            {
                continue;
            }

            var key = (item.Id, item.Bound);
            counts.TryGetValue(key, out var quantity);
            counts[key] = checked(quantity + item.Stack);
            if (representatives is not null)
            {
                representatives[key] = item;
            }
        }

        return counts;
    }
}
