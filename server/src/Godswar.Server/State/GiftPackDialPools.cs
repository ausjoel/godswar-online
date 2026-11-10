using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// W0 F-111 (the admin: "combine dial.ini's retail records with the current contents, plus a toggle").
/// Hot rate <c>box.dial_contents</c>: 0 = today's contents (default, unchanged), 1 = the original retail dial pools,
/// 2 = both combined (the retail pool joins the box's main pick with the same total weight as today's options).
/// The pool for each box is INFERRED from the pools' item levels and the boxes' own level text (GENERATED):
/// pool 1 = below Lv50, pools 2/5 = 50-80, 3/6 = 80-100, 4/7 = 100-120 (Iron/Minoan/Pandora of that band),
/// pools 8+9 (all levels, skill-book heavy) = the plain Iron Box / Minoan Chest / Pandora Box, pools 10-13
/// (materials) = Copper Chest (10+11), Silver Chest (12), Songkran Festival Box (13). Forbidden Soul 4107 keeps its
/// contents. Retail records keep their fixed appended attributes (Append + AppLV).
/// </summary>
internal static partial class GiftPackDialPools
{
    internal readonly record struct DialRecord(uint ItemId, int Quantity, short Bound, int[] Appends, int AppendLevel);

    public const string RateKey = "box.dial_contents";

    private static readonly IReadOnlyDictionary<uint, int[]> BoxPools = new Dictionary<uint, int[]>
    {
        [4400] = [1], [4401] = [2], [4402] = [3], [4403] = [4], [4404] = [8, 9],      // Iron Boxes
        [4405] = [1], [4406] = [5], [4407] = [6], [4408] = [7], [4409] = [8, 9],      // Minoan Chests
        [4410] = [1], [4411] = [2, 5], [4412] = [3, 6], [4413] = [4, 7], [4414] = [8, 9], // Pandora Boxes
        [4415] = [10, 11], [4418] = [10, 11],                                         // Copper Chests
        [4416] = [12],                                                                // Silver Chest
        [4417] = [13],                                                                // Songkran Festival Box
    };

    public static int Mode => (int)Math.Clamp(Application.Rates.ServerRates.Current.Get(RateKey), 0m, 2m);

    public static IReadOnlyList<DialRecord> PoolFor(uint boxId) =>
        BoxPools.TryGetValue(boxId, out var pools) ? pools.SelectMany(pool => Generated[pool]).ToArray() : [];

    /// <summary>The box definition for the current mode. Retail items the server does not know are left out.</summary>
    public static GiftPackDefinition Apply(uint boxId, GiftPackDefinition current, IItemTemplateCatalog templates, int mode)
    {
        if (mode == 0 || !BoxPools.ContainsKey(boxId)) return current;
        var retail = PoolFor(boxId).Where(record => templates.TryGet(record.ItemId, out _))
            .Select(record => new GiftPackReward(record.ItemId, record.Quantity, Weight: 1,
                Appends: record.Appends.Length == 0 ? null : record.Appends,
                AppendLevel: (short)record.AppendLevel, RetailBound: record.Bound))
            .ToArray();
        if (retail.Length == 0) return current;
        if (mode == 1)
            return current with { Rewards = [], Picks = [new GiftPackPick(1, retail)] };
        // Combined: the retail pool joins the first pick, weighted so retail and today's options are even overall.
        var picks = (current.Picks ?? []).ToArray();
        if (picks.Length == 0)
            return current with { Picks = [new GiftPackPick(1, retail)] };
        var today = picks[0].Options;
        var todayTotal = Math.Max(1, today.Sum(option => Math.Max(1, option.Weight)));
        var perRetail = Math.Max(1, (int)Math.Round(todayTotal / (double)retail.Length));
        var scaledToday = retail.Length * perRetail > todayTotal * 4
            ? today.Select(option => option with { Weight = Math.Max(1, option.Weight) * retail.Length }).ToArray()
            : today;
        picks[0] = picks[0] with { Options = [.. scaledToday, .. retail.Select(r => r with { Weight = perRetail })] };
        return current with { Picks = picks };
    }
}
