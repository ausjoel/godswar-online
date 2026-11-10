using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>Original AddEXP5 client identity, used only at item publication.</summary>
internal static class ExperiencePillItemContentBaseline
{
    public const int ItemId = 4174;
    public const string Texture = "./Localization/en_us/UI/Texture/Icon.gwo";
    public const string Icon = "612,900";

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
    [
        new(ItemId, "consume item", "AddEXP5", "Exp Pill", EquipmentSlot: 0,
            ClassIds: [], MinLevel: null, MaxLevel: null, Hand: null, SkillFlag: null,
            Texture, Icon, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["ID"] = "4174", ["Type"] = "consume item", ["Texture"] = Texture,
                ["Icon"] = Icon, ["Random"] = "0", ["Distribution"] = "0,0",
                ["Money"] = "0", ["Overlap"] = "99", ["Use"] = "1",
                ["BindType"] = "1", ["Skill"] = "5149"
            }))
    ];
}
