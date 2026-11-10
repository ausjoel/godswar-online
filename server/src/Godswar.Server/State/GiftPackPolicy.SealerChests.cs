namespace Godswar.Server.State;

/// <summary>
/// ROUND3 summons, the admin F-150 ("B-Gold flat 10-200x"): the Sacred Sealer's scroll-boss chests (Ladon 14066 Copper,
/// Cyclops 14067 Silver, Medusa 14068 Gold) promise B-Gold "at most 200x the Gold" paid for the scroll. Opening one
/// credits a flat (uniform) roll of 10x..200x the scroll's Gold price on top of the existing GENERATED pack items:
/// Ladon 180 Gold -> 1,800-36,000, Cyclops 900 -> 9,000-180,000, Medusa 1,800 -> 18,000-360,000 B-Gold.
/// Hand-written here (not in the generated pack file, which make_generated_packs.py rewrites).
/// </summary>
internal static partial class GiftPackPolicy
{
    internal const int SealerChestMinimumMultiple = 10;
    internal const int SealerChestMaximumMultiple = 200;

    /// <summary>Chest item -> the Gold price of the scroll whose boss drops it (SacredSealerPolicy prices), else 0.
    /// A method, not a field: Packs (GiftPackPolicy.cs) is built during type initialisation, before fields of this
    /// partial file are guaranteed to be set.</summary>
    internal static int SealerChestScrollPrice(uint chestItemId) => chestItemId switch
    {
        14066 => 180,
        14067 => 900,
        14068 => 1_800,
        _ => 0,
    };

    private static KeyValuePair<uint, GiftPackDefinition> WithSealerChestBindingGold(
        KeyValuePair<uint, GiftPackDefinition> entry) =>
        SealerChestScrollPrice(entry.Key) is var price and > 0
            ? new(entry.Key, entry.Value with
            {
                BindingGoldMinimum = price * SealerChestMinimumMultiple,
                BindingGoldMaximum = price * SealerChestMaximumMultiple,
            })
            : entry;

    /// <summary>F-150: the B-Gold one open of <paramref name="definition"/> pays (0 when it pays none).</summary>
    internal static int RollBindingGold(GiftPackDefinition definition, Random random) =>
        definition.BindingGoldMaximum <= 0
            ? 0
            : random.Next(Math.Max(0, definition.BindingGoldMinimum), definition.BindingGoldMaximum + 1);
}
