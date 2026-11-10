using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>Released eight-tier declaration; item appearances remain sealed for exact forward migration.</summary>
internal static class HolySuitContentBaselineV2
{
    internal const int DiviniumWareItemId = 9017;
    internal const string WareTexture = "./Localization/en_us/UI/Texture/HolySuitWare.gwo";
    internal const string DiviniumIcon = "108,0";
    private const string ExtensionSource = "holy-suit-divinium-v1-20260910";

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } = HolySuitContentBaselineV1.ItemTemplates
        .Select(item => item.Id switch
        {
            9014 => WareAppearance(item, "RuneSteel Ware", "0,0"),
            9015 => WareAppearance(item, "Arcanite Ware", "36,0"),
            9016 => WareAppearance(item, "Seraphite Ware", "72,0"),
            _ => item
        }).Append(CreateDiviniumWare()).OrderBy(item => item.Id).ToArray();

    public static IReadOnlyList<HolySuitTierDefinition> Tiers { get; } = HolySuitContentBaselineV1.Tiers
        .Select(tier => tier.SuitType switch
        {
            5 => tier with { Name = "RuneSteel" },
            6 => tier with { Name = "Arcanite" },
            7 => tier with { Name = "Seraphite" },
            _ => tier
        }).Append(new(8, "Divinium", 10, DiviniumWareItemId, ExtensionSource)).ToArray();

    public static IReadOnlyList<HolySuitConsumableDefinition> Consumables { get; } = HolySuitContentBaselineV1.Consumables
        .Append(new(DiviniumWareItemId, HolySuitConsumableRole.Ware, 8, 0, 99, 0, ExtensionSource))
        .OrderBy(item => item.ItemId).ToArray();

    public static HolySuitOperationPolicy OperationPolicy => HolySuitContentBaselineV1.OperationPolicy;

    public static IReadOnlyList<HolySuitUpgradeDefinition> Upgrades { get; } = HolySuitContentBaselineV1.Upgrades
        .Concat(Enumerable.Range(1, 10).Select(level => new HolySuitUpgradeDefinition(
            level == 1 ? (short)7 : (short)8, level == 1 ? (short)10 : checked((short)(level - 1)),
            8, checked((short)level), 0, DiviniumWareItemId, checked((short)level),
            96 + 3 * level, ExtensionSource))).ToArray();

    private static ItemTemplateSeed CreateDiviniumWare()
    {
        var previous = HolySuitContentBaselineV1.ItemTemplates.Single(item => item.Id == 9016);
        var result = WareAppearance(previous with { Id = DiviniumWareItemId, NameKey = "Shenqi9017" },
            "Divinium Ware", DiviniumIcon);
        return result;
    }

    private static ItemTemplateSeed WareAppearance(ItemTemplateSeed item, string name, string icon)
    {
        var stats = JsonSerializer.Deserialize<Dictionary<string, string>>(item.StatsJson)!;
        stats["Texture"] = WareTexture;
        stats["ID"] = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        stats["Icon"] = icon;
        return item with { DisplayName = name, Texture = WareTexture, Icon = icon, StatsJson = JsonSerializer.Serialize(stats) };
    }
}
