using System.Collections.Frozen;
using System.Globalization;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Wild baby-pet capture (IGG guide "Pet System", acid=136: "Possible locations ... include the suburbs of
/// Athens and Sparta, Marathon, Parnitha, Megara, Argolis, Nemea, Peloponnesus, and Medusa Island";
/// tools Wooden / Iron / Silver / Gold / Mystic, "capturing tools ... break regardless of success").
/// </summary>
/// <remarks>
/// The capturable monsters are the client's own <c>[Pet]</c> templates (<c>Localization/en_us/Monster/&lt;scene&gt;/Monster.ini</c>,
/// published with is_pet = true). The template key alone does not name the species: <c>A_normal_male_001</c> is a
/// Flower Pixie on Marathon but a Minotaur on Megara, so the table is keyed by (map, template) and was read from the
/// client name and model (<c>flower_*</c> = Flower Pixie, <c>cow_*</c> = Minotaur). Medusa (200), Atlantis (205) and
/// the Lelantine Farm (42) keep their own capture rules and never enter this path.
/// Switch: server rate <c>pet.wild_capture</c> (0 = off, the default).
/// </remarks>
internal static class WildPetCapturePolicy
{
    public const uint FlowerPixieEggItemId = 10151;
    public const uint MinotaurEggItemId = 10152;
    public const int FlowerPixieSpeciesId = 2;
    public const int MinotaurSpeciesId = 3;

    private static readonly FrozenDictionary<(short MapId, string TemplateKey), uint> EggByTemplate =
        new Dictionary<(short MapId, string TemplateKey), uint>
        {
            [(2, "A_normal_female_001")] = FlowerPixieEggItemId,   // Athens_Newbie  [Pet]Flower Pixie   flower_female_001
            [(2, "A_normal_male_001")] = MinotaurEggItemId,        // Athens_Newbie  [Pet]Minotaur       cow_male_001
            [(3, "A_normal_male_001")] = MinotaurEggItemId,        // Parnitha_1     [Pet] Baby Minotaur cow_male_001
            [(4, "A_normal_female_001")] = FlowerPixieEggItemId,   // Sparta_Newbie  [Pet]Flower Pixie   flower_female_001
            [(4, "A_normal_male_001")] = MinotaurEggItemId,        // Sparta_Newbie  [Pet]Minotaur       cow_male_001
            [(5, "A_normal_female_001")] = MinotaurEggItemId,      // Nemea_1        [Pet] Baby Minotaur cow_female_001
            [(11, "A_normal_male_001")] = FlowerPixieEggItemId,    // Marathon_All   [Pet] Baby Flower Pixie flower_female_001
            [(13, "A_normal_female_001")] = FlowerPixieEggItemId,  // Peloponnese_All [Pet] Baby Flower Pixie flower_female_001
            [(16, "A_normal_female_001")] = FlowerPixieEggItemId,  // Argolis_All    [Pet] Baby Flower Pixie flower_female_001
            [(16, "A_normal_male_001")] = MinotaurEggItemId,       // Argolis_All    [Pet] Baby Minotaur cow_male_001
            [(18, "A_normal_female_001")] = FlowerPixieEggItemId,  // Megara_All     [Pet] Baby Flower Pixie flower_female_001
            [(18, "A_normal_male_001")] = MinotaurEggItemId        // Megara_All     [Pet] Baby Minotaur cow_female_001
        }.ToFrozenDictionary();

    public static IEnumerable<(short MapId, string TemplateKey)> Templates => EggByTemplate.Keys;

    public static bool Enabled => ServerRates.Current.Absolute(ServerRateKeys.WildPetCapture) == 1;

    /// <summary>True for the maps that hold wild baby pets (ignores the switch).</summary>
    public static bool IsWildMap(int mapId) => mapId is 2 or 3 or 4 or 5 or 11 or 13 or 16 or 18;

    /// <summary>The egg a capturable wild baby pet yields; ignores the switch.</summary>
    public static bool TryGetEgg(int mapId, string? templateKey, out uint eggItemId)
    {
        eggItemId = 0;
        return templateKey is not null && mapId is >= short.MinValue and <= short.MaxValue &&
            EggByTemplate.TryGetValue(((short)mapId, templateKey), out eggItemId);
    }

    public static bool IsWildPetTemplate(int mapId, string? templateKey) => TryGetEgg(mapId, templateKey, out _);

    public static int SpeciesForEgg(uint eggItemId) => eggItemId switch
    {
        FlowerPixieEggItemId => FlowerPixieSpeciesId,
        MinotaurEggItemId => MinotaurSpeciesId,
        _ => 0
    };

    /// <summary>
    /// GENERATED capture chance per tool. The guide only ranks them ("Negligible Effect" / "by a little" /
    /// "Fairly" / "quite a bit" / "Greatly"; the wiki: Slight / Minor / Significant / Substantial / approaching
    /// 100% for the Mysterious Tuck Net), so the percentages are invented. Override with
    /// <c>GODSWAR_WILD_PET_CAPTURE_CHANCES=30,40,55,70,95</c> (percent, Wooden..Tuck Net; read at start-up).
    /// A miss uses the tool up, gives no egg, and the pet runs off.
    /// </summary>
    public static double CaptureChance(int netItemId) =>
        netItemId is >= 10080 and <= 10084 ? Chances[netItemId - 10080] : 0.0;

    private static readonly double[] DefaultChances = [0.30, 0.40, 0.55, 0.70, 0.95];

    private static readonly double[] Chances = ParseChances(
        Environment.GetEnvironmentVariable("GODSWAR_WILD_PET_CAPTURE_CHANCES"));

    internal static double[] ParseChances(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DefaultChances;
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 5) return DefaultChances;
        var values = new double[5];
        for (var i = 0; i < 5; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ||
                percent is < 0 or > 100)
            {
                return DefaultChances;
            }
            values[i] = percent / 100.0;
        }
        return values;
    }

    /// <summary>
    /// GENERATED aptitude weights (basis points, total 10,000) for a captured baby egg, over the eleven native
    /// aptitudes the Medusa table also uses. The guide only says aptitude shows as the name colour; it gives no
    /// odds. Low aptitudes are common, the top ones rare. The tool does not change the aptitude (the guide says
    /// tools only change the success rate). Aptitudes the species has no native profile for are skipped.
    /// </summary>
    public static IReadOnlyList<(short Aptitude, int BasisPoints)> AptitudeWeights { get; } =
    [
        (1, 4000), (2, 2500), (3, 1500), (4, 800), (5, 500), (7, 300),
        (8, 200), (9, 100), (10, 60), (12, 30), (14, 10)
    ];

    /// <summary>GENERATED: a caught or killed baby pet comes back after five minutes.</summary>
    public static readonly TimeSpan RespawnInterval = TimeSpan.FromMinutes(5);

    /// <summary>The respawn wait for a wild baby pet while the switch is on; otherwise the caller keeps its own.</summary>
    public static bool TryGetRespawnInterval(int mapId, string? templateKey, out TimeSpan interval)
    {
        interval = RespawnInterval;
        return Enabled && IsWildPetTemplate(mapId, templateKey);
    }

    /// <summary>
    /// Whether an operator-authored (gm_monster_spawns) row may use this pet template. Pet models normally stay
    /// out of authored spawns; the generated baby-pet rows (migration.sql) load only while the switch is on when
    /// the map is first loaded. The captured map 2 pets are published spawns and never pass through here.
    /// </summary>
    public static bool AdmitsAuthoredSpawn(int mapId, string? templateKey) =>
        Enabled && IsWildPetTemplate(mapId, templateKey);
}
