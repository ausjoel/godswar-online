namespace Godswar.Server.Application.Seasons;

/// <summary>
/// W3 Christmas (the admin F-26, F-134): Santa Claus (Athens_035 / Sparta_035, function 109, client NpcFunSDAN.lua) and
/// each faction's Gigantic Reindeer Model. Pure rules here; the clicks are GameClientHandler.Christmas.cs.
/// <para>
/// The client draws a reply on the page whose Index equals the replies since the window opened, so every click gets
/// exactly one reply: page 1 [1 gloves, 2 model] or [100] under Lv30; page 2 gloves [102 | 103 | 101] or the model
/// menu [3, 4, 5]; page 3 turn-in [105 + 1000*done | 104 | 106], progress [1000*lvl+108, 1000*points+116] or [107],
/// claim menu [6..10] or [109]; page 4 turn-in result [110 | 111 | 112 | 1100] or claim [113 | 114 | 1000*lvl+115].
/// </para>
/// </summary>
internal static class ChristmasRules
{
    public const int MinimumLevel = 30;          // NF_L0_SD100
    public const int GlovesPerDay = 30;          // NF_L0_SD102
    public const int AntlersPerDay = 100;        // NF_L0_SD104 / SD1053
    public const int MaxLevel = 5;               // NF_L0_SD1001
    public const int SantaGloves = 10086;
    public const int Antler = 4005;

    /// <summary>The client's own thresholds (NpcFunSDAN.lua luatext).</summary>
    public static readonly int[] ClientThresholds = [5000, 10000, 20000, 30000, 50000];

    /// <summary>
    /// The thresholds this server uses: the client's scaled by season.christmas_threshold_percent (GENERATED 10 %:
    /// 500 / 1,000 / 2,000 / 3,000 / 5,000, reachable by about ten players in the ten-day window). Each at least 1
    /// and strictly above the one before.
    /// </summary>
    public static int[] Thresholds(int percent)
    {
        var clamped = Math.Clamp(percent, 1, 100);
        var result = new int[ClientThresholds.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var scaled = (int)Math.Max(1L, (long)ClientThresholds[i] * clamped / 100);
            result[i] = i > 0 && scaled <= result[i - 1] ? result[i - 1] + 1 : scaled;
        }

        return result;
    }

    /// <summary>Model level 0-5 for a point total.</summary>
    public static int Level(int points, int[] thresholds) => thresholds.Count(threshold => points >= threshold);

    /// <summary>Points still accepted before the model is finished.</summary>
    public static int Room(int points, int[] thresholds) => Math.Max(0, thresholds[^1] - points);

    /// <summary>Page 3 after "Turn in antlers": the typed-amount form, or why there is none.</summary>
    public static int[] TurnInPage(int doneToday, int modelPoints, int[] thresholds) =>
        Room(modelPoints, thresholds) == 0 ? [106]
        : doneToday >= AntlersPerDay ? [104]
        : [105 + 1000 * Math.Max(0, doneToday)];

    /// <summary>
    /// Page 3 after "Progress". At level 5 only 107: the 108 line indexes luatext[level + 1], which is nil at 5
    /// (a Lua error in the client).
    /// </summary>
    public static int[] ProgressPage(int points, int[] thresholds)
    {
        var level = Level(points, thresholds);
        return level >= MaxLevel ? [107] : [1000 * level + 108, 1000 * Math.Max(0, points) + 116];
    }

    /// <summary>Page 3 after "Claim rewards": the five level buttons, or 109 while the model is at level 0.</summary>
    public static int[] ClaimMenu(int points, int[] thresholds) =>
        Level(points, thresholds) == 0 ? [109] : [6, 7, 8, 9, 10];

    /// <summary>Model level a page-3 claim button (6-10) stands for, or 0.</summary>
    public static int ClaimLevel(int button) => button is >= 6 and <= 10 ? button - 5 : 0;

    /// <summary>
    /// Level-claim reward (GENERATED; the client names none): level x 100 B-Gold and level x 100,000 EXP (EXP at
    /// exp.events). No items, because page 4 has no bag-full line.
    /// </summary>
    public static (int BindingGold, int Experience) ClaimReward(int level) => (level * 100, level * 100_000);

    /// <summary>
    /// Turn-in reward (NF_L0_SD111 "experience as well as talent points"; amounts GENERATED): EXP per antler from
    /// season.christmas_antler_exp, and 1 Talent Point per 10 antlers (at least 1).
    /// </summary>
    public static (long Experience, int TalentPoints) TurnInReward(int antlers, int experiencePerAntler) =>
        antlers <= 0 ? (0, 0) : ((long)antlers * Math.Max(0, experiencePerAntler), Math.Max(1, antlers / 10));

    /// <summary>Monsters that drop Antlers in season: Athens' Little Deer and Sparta-suburb Vicious Fawn.</summary>
    public static bool DropsAntlers(short mapId, string templateKey) =>
        (mapId, templateKey) is (1, "A_normal_deer_001") or (4, "A_normal_xedeer_001");
}
