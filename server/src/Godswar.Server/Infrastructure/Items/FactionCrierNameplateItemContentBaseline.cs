using System.Globalization;
using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// Reviewed stock-client Nameplates. Runtime inventory authority consumes the
/// sealed PostgreSQL item publication; these seeds only create that release.
/// </summary>
internal static class FactionCrierNameplateItemContentBaseline
{
    public const int FirstItemId = 3820;
    public const int LastItemId = 3825;
    public const short MaximumStack = 99;
    public const string ItemType = "consume item";
    public const string Texture =
        "./Localization/en_us/UI/Texture/Icon.gwo";

    private static readonly (string Icon, int Ordinal)[] Reviewed =
    [
        ("216,936", 1),
        ("252,936", 2),
        ("288,936", 3),
        ("324,936", 4),
        ("360,936", 5),
        ("396,936", 6)
    ];

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        Reviewed.Select(Create).ToArray();

    private static ItemTemplateSeed Create(
        (string Icon, int Ordinal) item)
    {
        var id = FirstItemId + item.Ordinal - 1;
        var stats = new Dictionary<string, string>
        {
            ["ID"] = id.ToString(CultureInfo.InvariantCulture),
            ["Type"] = ItemType,
            ["Texture"] = Texture,
            ["Icon"] = item.Icon,
            ["Random"] = "0",
            ["Distribution"] = "150,200",
            ["Money"] = "0",
            ["Overlap"] = MaximumStack.ToString(
                CultureInfo.InvariantCulture),
            ["BindType"] = "1"
        };
        return new ItemTemplateSeed(
            id,
            ItemType,
            $"Nameplate{item.Ordinal}",
            $"Nameplate {item.Ordinal}",
            EquipmentSlot: 0,
            ClassIds: [],
            MinLevel: null,
            MaxLevel: null,
            Hand: null,
            SkillFlag: null,
            Texture,
            item.Icon,
            JsonSerializer.Serialize(stats));
    }
}
