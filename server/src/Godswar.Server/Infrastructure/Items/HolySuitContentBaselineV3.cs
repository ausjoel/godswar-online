using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>Released material names and original Experience Prism presentation.</summary>
internal static class HolySuitContentBaselineV3
{
    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } = HolySuitContentBaselineV2.ItemTemplates
        .Select(item => item.Id switch
        {
            9014 => item with { DisplayName = "RuneSteel Ingot" },
            9015 => item with { DisplayName = "Arcanite Crystal" },
            9016 => item with { DisplayName = "Seraphite Core" },
            9017 => item with { DisplayName = "Divinium Essence" },
            _ => item
        }).ToArray();
}
