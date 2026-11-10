using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// What the box dial shows. The reward is already rolled and committed by the keyed gift pack open;
/// the ring only presents it, with the rest filled from the same box's list.
/// </summary>
internal static class GoldBoxDialPlan
{
    public const int RingSize = 16;

    // GODSWAR_GOLD_BOX_DIAL: "all", or account ids separated by commas. Unset: boxes get a bag refresh only.
    private static readonly string? Gate = Environment.GetEnvironmentVariable("GODSWAR_GOLD_BOX_DIAL");

    public static bool IsEnabledFor(int accountId) => IsEnabledFor(Gate, accountId);

    internal static bool IsEnabledFor(string? gate, int accountId)
    {
        if (string.IsNullOrWhiteSpace(gate) || accountId <= 0) return false;
        if (gate.Trim() == "all") return true;
        return gate.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => int.TryParse(value, out var id) && id == accountId);
    }

    /// <summary>Seventeen items (ring 0-15, centre 16) and the ring index of the first reward.</summary>
    public static (CompactItemEntry[] Items, int Target) Build(GiftPackDefinition pack,
        IReadOnlyList<(uint ItemId, int Quantity, short Bound)> rewards, CompactItemEntry box, int profession,
        bool male, Func<uint, bool> knownItem, Random random)
    {
        if (rewards.Count == 0) throw new ArgumentException("A dial needs the committed reward.", nameof(rewards));
        var positions = Enumerable.Range(0, RingSize).OrderBy(_ => random.Next()).ToArray();
        var ring = new CompactItemEntry[RingSize];
        // Boxes give one to three items. All of them are on the ring; the wheel stops on the first.
        var placed = 0;
        foreach (var reward in rewards.Take(RingSize))
            ring[positions[placed++]] = Entry(reward.ItemId, reward.Quantity, reward.Bound);

        bool Fits(GiftPackReward option) => (option.ClassMask >> profession & 1) != 0;
        var candidates = pack.Rewards.Concat((pack.Picks ?? []).SelectMany(pick => pick.Options)).Where(Fits)
            .Select(option => male && option.MaleItemId != 0 ? option with { ItemId = option.MaleItemId } : option)
            .Where(option => option.ItemId != 0 && knownItem(option.ItemId))
            .ToList();
        var fresh = candidates.Where(option => rewards.All(reward => reward.ItemId != option.ItemId))
            .GroupBy(option => option.ItemId).Select(group => group.First()).ToList();
        while (placed < RingSize)
        {
            // Distinct items first; a short list repeats, and an empty one repeats the rewards.
            var pool = fresh.Count > 0 ? fresh : candidates;
            CompactItemEntry decoy;
            if (pool.Count == 0)
            {
                var reward = rewards[random.Next(rewards.Count)];
                decoy = Entry(reward.ItemId, reward.Quantity, reward.Bound);
            }
            else
            {
                var option = Pick(pool, random);
                if (pool == fresh) fresh.Remove(option);
                decoy = Entry(option.ItemId, option.Quantity, box.Bound);
            }
            ring[positions[placed++]] = decoy;
        }

        var items = ring.Append(Entry(box.Id, 1, box.Bound)).ToArray();
        return (items, positions[0]);
    }

    private static GiftPackReward Pick(List<GiftPackReward> pool, Random random)
    {
        var total = pool.Sum(option => Math.Max(1, option.Weight));
        var roll = random.Next(total);
        foreach (var option in pool)
        {
            roll -= Math.Max(1, option.Weight);
            if (roll < 0) return option;
        }
        return pool[^1];
    }

    private static CompactItemEntry Entry(uint itemId, int quantity, short bound) =>
        CompactItemEntry.Empty with
        {
            Id = itemId, Quality = 1, Grade = 1, Bound = (short)(bound == 0 ? 0 : 1),
            Stack = (short)Math.Clamp(quantity, 1, 99)
        };

    /// <summary>
    /// Where the client parks the reward while the wheel turns: the box's own slot when its last one
    /// was used, otherwise the first empty slot from before the open. Cosmetic until the bag refresh.
    /// </summary>
    public static int RewardSlot(string bagBefore, int boxSlot, CompactItemEntry box)
    {
        if (box.Stack <= 1) return boxSlot;
        for (var slot = 0; slot < 96; slot++)
            if (KitBagSlots.GetItem(bagBefore, slot).IsEmpty) return slot;
        return boxSlot;
    }
}
