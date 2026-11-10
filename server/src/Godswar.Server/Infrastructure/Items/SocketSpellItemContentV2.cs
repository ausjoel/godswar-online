using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

// Preserve the stock shared-icon baseline for immutable historical releases.
internal static class SocketSpellItemContentV2
{
    public const string Texture = "./Localization/en_us/UI/Texture/SocketSpells.gwo";
    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        SocketSpellItemContentBaseline.ItemTemplates.Select(Upgrade).ToArray();

    private static ItemTemplateSeed Upgrade(ItemTemplateSeed seed)
    {
        var icon = $"{(seed.Id - SocketSpellItemContentBaseline.FirstItemId) * 36},0";
        var stats = JsonSerializer.Deserialize<Dictionary<string, string>>(seed.StatsJson)!;
        stats["Texture"] = Texture;
        stats["Icon"] = icon;
        return seed with { Texture = Texture, Icon = icon, StatsJson = JsonSerializer.Serialize(stats) };
    }
}
