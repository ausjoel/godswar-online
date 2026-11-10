using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

internal static class LegacyInstanceOpalItemContentBaseline
{
    public const int ItemId = 3932;
    public const short MaximumStack = 99;

    public static ItemTemplateSeed ItemTemplate { get; } = new(
        ItemId,
        "consume item",
        "Earphone3932",
        "Opal",
        EquipmentSlot: 0,
        ClassIds: [],
        MinLevel: null,
        MaxLevel: null,
        Hand: null,
        SkillFlag: null,
        "./Localization/en_us/UI/Texture/Icon.gwo",
        "540,900",
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ID"] = "3932",
            ["Type"] = "consume item",
            ["Texture"] =
                "./Localization/en_us/UI/Texture/Icon.gwo",
            ["Icon"] = "540,900",
            ["Random"] = "0",
            ["Distribution"] = "0,0",
            ["Money"] = "0",
            ["Overlap"] = "99"
        }));
}
