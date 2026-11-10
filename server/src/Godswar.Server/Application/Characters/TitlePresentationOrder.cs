using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Application.Characters;

internal enum TitleCosmeticRarity
{
    Ordinary, Uncommon, Rare, Epic, Legendary, Mythic, Sovereign
}

/// <summary>
/// Authored cosmetic rarity: Wonderland follows island progression; Medusa
/// follows its existing purple/orange/crimson tiers. This changes neither
/// ownership, selection, attributes nor installed client colors.
/// </summary>
internal static class TitlePresentationOrder
{
    private static readonly IReadOnlyDictionary<uint, int> WonderlandIslands =
        Enumerable.Range(1, 8).Select(WonderlandTitlePolicy.Resolve)
            .ToDictionary(static title => title.TitleId, static title => title.IslandNumber);

    internal static TitleCosmeticRarity RarityRank(uint titleId) => ProgressionRank(titleId) switch
    {
        1 => TitleCosmeticRarity.Uncommon,
        2 or 3 => TitleCosmeticRarity.Rare,
        4 or 5 => TitleCosmeticRarity.Epic,
        6 => TitleCosmeticRarity.Legendary,
        7 => TitleCosmeticRarity.Mythic,
        8 => TitleCosmeticRarity.Sovereign,
        _ => titleId switch
        {
            5152 => TitleCosmeticRarity.Mythic,
            5153 or 5154 => TitleCosmeticRarity.Legendary,
            5009 or 5010 or 5011 => TitleCosmeticRarity.Epic,
            _ => TitleCosmeticRarity.Ordinary
        }
    };

    // Later Wonderland milestones lead their rarity group. Other titles have
    // no island progression, so the packet's final ID key keeps their ties stable.
    internal static int ProgressionRank(uint titleId) => WonderlandIslands.GetValueOrDefault(titleId);
}
