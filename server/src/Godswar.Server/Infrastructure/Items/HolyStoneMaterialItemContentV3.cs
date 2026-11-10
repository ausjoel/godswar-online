using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

// Forward-only presentation release. Keep the v2 seeds intact: they are the
// reviewed predecessor used by sealed manifests and mutable identity checks.
internal static class HolyStoneMaterialItemContentV3
{
    public const string Texture = "./Localization/en_us/UI/Texture/HolyStoneReagents.gwo";
    public static readonly int[] ReagentIds = [9040, 9041, 9042, 9050, 9051, 9052, 9053, 9054];

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        HolyStoneMaterialItemContentBaseline.ItemTemplates.Select(Upgrade).ToArray();

    private static ItemTemplateSeed Upgrade(ItemTemplateSeed seed)
    {
        var index = Array.IndexOf(ReagentIds, seed.Id);
        if (index < 0) return seed;
        var icon = $"{index * 36},0";
        var stats = JsonSerializer.Deserialize<Dictionary<string, string>>(seed.StatsJson)!;
        stats["Texture"] = Texture;
        stats["Icon"] = icon;
        return seed with
        {
            DisplayName = seed.Id == 9054 ? "Platinum Evasion Signet" : seed.DisplayName,
            Texture = Texture,
            Icon = icon,
            StatsJson = JsonSerializer.Serialize(stats)
        };
    }
}
