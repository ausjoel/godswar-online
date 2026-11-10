using System.Globalization;
using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>Original instancebox1–12 client identities, used only at publication.</summary>
internal static class WonderlandSackItemContentBaseline
{
    public const int FirstItemId = 4450;
    public const int LastItemId = 4461;
    public const string Texture = "./Localization/en_us/UI/Texture/Icon.gwo";

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        new (string Name, string Icon)[]
        {
            ("Alpha Demon's Sack", "936,252"),
            ("Capritaur Ajax's Sack", "720,684"),
            ("Flamingo King's Sack", "684,252"),
            ("Angry Cyclops' Treasure", "432,756"),
            ("General Nyx's Treasure (Sparta)", "612,360"),
            ("General Aeson's Treasure (Athens)", "612,360"),
            ("Platinum Dragon's Collection", "792,252"),
            ("Dracoladon's Chest", "612,720"),
            ("Minotaur King's Envelope", "612,252"),
            ("Titan's Xmas Deer Chest", "540,720"),
            ("Dracolord's Chest", "612,720"),
            ("Scorpion King's Chest", "396,756")
        }.Select((value, index) => Create(index, value.Name, value.Icon)).ToArray();

    private static ItemTemplateSeed Create(int index, string name, string icon)
    {
        var id = FirstItemId + index;
        var stats = new Dictionary<string, string>
        {
            ["ID"] = id.ToString(CultureInfo.InvariantCulture),
            ["Type"] = "consume item",
            ["Texture"] = Texture,
            ["Icon"] = icon,
            ["Random"] = "0",
            ["Distribution"] = "0,0",
            ["Money"] = "0",
            ["Overlap"] = "99",
            ["Use"] = "1",
            ["BindType"] = "1",
            ["Skill"] = (5400 + index).ToString(CultureInfo.InvariantCulture)
        };
        return new ItemTemplateSeed(id, "consume item", $"instancebox{index + 1}", name,
            EquipmentSlot: 0, ClassIds: [], MinLevel: null, MaxLevel: null,
            Hand: null, SkillFlag: null, Texture, icon, JsonSerializer.Serialize(stats));
    }
}
