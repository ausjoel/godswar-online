using System.Collections.Frozen;

namespace Godswar.Server.State;

internal readonly record struct ProfessionMaterial(uint ItemId, int Count);

/// <summary>
/// PROF (q09): one client blueprint (Lifing_Blueprint.xml). Materials, product, count and odds are the client
/// fields verbatim. RecipeLevel is the field "Lv" and ExperienceUntilLevel "SkillLv"; reading them as "profession
/// level needed" and "last profession level that still earns exp" is an inference, so they are reported, not enforced.
/// </summary>
internal sealed record ProfessionBlueprint(
    int Id,
    byte Profession,
    byte Category,
    byte RecipeLevel,
    byte ExperienceUntilLevel,
    byte Type,
    int ActivityPoints,
    ProfessionMaterial[] Materials,
    uint ProductId,
    int ProductCount,
    int OddsPercent,
    uint RecipeItemId);

/// <summary>
/// PROF (q09): the profession recipe data and the checks a craft needs. Callers: the GM command /craft (rig only,
/// GODSWAR_PROFESSIONS=1) and, since PROF slice 1, the client's own Manufacture window (messages 10334-10341,
/// GameClientHandler.Professions.cs, docs/professions/README.md).
/// </summary>
internal static partial class ProfessionBlueprintCatalog
{
    public const string EnabledVariable = "GODSWAR_PROFESSIONS";

    /// <summary>Off unless GODSWAR_PROFESSIONS=1. Settable for the protocol checks.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable(EnabledVariable) == "1";

    public static readonly string[] ProfessionNames = ["", "Alchemy", "Smithing", "Artificer", "Scholar"];

    // Lifing_Skill.xml UpExp: character level shown next to each profession level (PlayLv). Informational.
    private static readonly int[] CharacterLevelByProfessionLevel =
    [
        0, 50, 50, 50, 50, 50, 50, 55, 60, 65, 70, 75, 80, 84, 88, 92, 96, 100, 103, 106, 109, 112, 115, 118, 120,
        122, 124, 126, 128, 130, 130
    ];

    // Built on first use: the order of static initialisers across partial files is not defined.
    private static FrozenDictionary<int, ProfessionBlueprint>? _byId;

    private static FrozenDictionary<int, ProfessionBlueprint> ById =>
        _byId ??= Shipped.ToFrozenDictionary(static blueprint => blueprint.Id);

    public static IReadOnlyList<ProfessionBlueprint> All => Shipped;

    public static bool TryGet(int id, out ProfessionBlueprint blueprint) => ById.TryGetValue(id, out blueprint!);

    public static IEnumerable<ProfessionBlueprint> Producing(uint itemId) =>
        Shipped.Where(blueprint => blueprint.ProductId == itemId);

    public static int CharacterLevelFor(byte professionLevel) =>
        professionLevel < CharacterLevelByProfessionLevel.Length ? CharacterLevelByProfessionLevel[professionLevel] : 0;

    /// <summary>Materials still missing from a bag, given the bag's total count per item id.</summary>
    public static IReadOnlyList<ProfessionMaterial> Missing(
        ProfessionBlueprint blueprint,
        IReadOnlyDictionary<uint, int> bagCounts,
        int times = 1)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(bagCounts);
        var missing = new List<ProfessionMaterial>();
        foreach (var material in blueprint.Materials)
        {
            var have = bagCounts.TryGetValue(material.ItemId, out var count) ? count : 0;
            var need = checked(material.Count * times);
            if (have < need)
            {
                missing.Add(new(material.ItemId, need - have));
            }
        }

        return missing;
    }

    /// <summary>Every material and product id that the server's item templates do not know.</summary>
    public static IReadOnlyList<string> Validate(Func<uint, bool> templateExists)
    {
        ArgumentNullException.ThrowIfNull(templateExists);
        var problems = new List<string>();
        foreach (var blueprint in Shipped)
        {
            if (blueprint.Profession is < 1 or > 4 || blueprint.Materials.Length is < 1 or > 4 ||
                blueprint.ProductCount < 1 || blueprint.OddsPercent is < 1 or > 100)
            {
                problems.Add($"blueprint {blueprint.Id}: malformed");
            }

            foreach (var material in blueprint.Materials)
            {
                if (material.Count < 1 || !templateExists(material.ItemId))
                {
                    problems.Add($"blueprint {blueprint.Id}: material {material.ItemId} x{material.Count}");
                }
            }

            if (!templateExists(blueprint.ProductId))
            {
                problems.Add($"blueprint {blueprint.Id}: product {blueprint.ProductId}");
            }
        }

        return problems;
    }

    /// <summary>The client's Odds field as a percent roll (0..99 below Odds succeeds).</summary>
    public static bool RollSucceeds(ProfessionBlueprint blueprint, int roll0To99) =>
        roll0To99 < blueprint.OddsPercent;
}
