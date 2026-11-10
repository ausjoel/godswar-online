namespace Godswar.LootTool;

/// <summary>One of the 16 aptitude tiers, with both name spaces side by side.</summary>
/// <param name="ClientChinese">
/// What the installed client actually paints for this numeric value
/// (<c>Localization\zh_cn\UI\Base\text.lua</c>, <c>PETAPTITUDE1..16</c>). The
/// project renamed tiers 6-10 in <c>docs/pet-system-foundation.md</c>, so this
/// column deliberately disagrees with <see cref="ProjectChinese"/> for 6-10.
/// </param>
internal sealed record PetAptitudeInfo(
    short Value,
    string ProjectName,
    string ProjectChinese,
    string ClientChinese);

/// <summary>
/// Pet metadata that lives in code rather than in the database: the client's own
/// talent labels and the tier rule the <c>character_pets</c> check constraint
/// <c>ck_character_pets_quality_innate_talents</c> enforces.
/// </summary>
internal static class PetContent
{
    /// <summary>Talent bits 1/2/4/8/16, named as the client names them.</summary>
    public static readonly (int Bit, string Name)[] Talents =
    [
        (1, "随机事件"),
        (2, "任务派遣"),
        (4, "打工"),
        (8, "治愈"),
        (16, "合体")
    ];

    public static readonly IReadOnlyList<PetAptitudeInfo> Aptitudes =
    [
        new(1, "Weak", "弱小", "懦弱型"),
        new(2, "Fool", "愚蠢", "愚钝型"),
        new(3, "Cowish", "牛劲", "胆怯型"),
        new(4, "Moderate", "平平", "中庸型"),
        new(5, "Rational", "理智", "理智型"),
        new(6, "Calm", "沉稳", "冷静型"),
        new(7, "Grumpy", "暴躁", "聪慧型"),
        new(8, "Brave", "勇猛", "热情型"),
        new(9, "Zealous", "狂热", "暴躁型"),
        new(10, "Smart", "聪明", "无畏型"),
        new(11, "Overbearing", "霸道", "霸道型"),
        new(12, "Ferocious", "凶猛", "凶猛型"),
        new(13, "Almighty", "全能", "万能型"),
        new(14, "Godly", "神级", "众神型"),
        new(15, "Celestial", "天界（项目扩展档）", "备用"),
        new(16, "Transcendent", "超脱（项目扩展档）", "备用")
    ];

    public const short MinimumAptitude = 1;

    public const short MaximumAptitude = 16;

    /// <summary>
    /// The mask rule the database itself enforces: 1-9 none, 10-13 dispatch +
    /// healing + merge, 14-16 all five.
    /// </summary>
    public static short TalentMaskFor(short aptitude) =>
        aptitude switch
        {
            >= 14 => 31,
            >= 10 => 26,
            _ => 0
        };

    public static string DescribeTalentMask(int mask) =>
        mask == 0
            ? "无天赋"
            : string.Join("、", Talents
                .Where(talent => (mask & talent.Bit) == talent.Bit)
                .Select(talent => talent.Name));

    public static string DescribeAptitude(short aptitude)
    {
        var info = Aptitudes.FirstOrDefault(candidate => candidate.Value == aptitude);
        return info is null
            ? $"{aptitude}（未知档）"
            : $"{aptitude}｜项目 {info.ProjectChinese} {info.ProjectName}｜客户端显示 {info.ClientChinese}";
    }

    public static string FoodKind(short kind) => kind switch
    {
        1 => "草食",
        2 => "肉食",
        3 => "杂食",
        _ => $"未知({kind})"
    };

    public static string StatName(int code) => code switch
    {
        1 => "敏捷",
        2 => "力量",
        3 => "命中",
        4 => "技巧",
        5 => "智慧",
        6 => "幸运",
        _ => $"未知({code})"
    };
}
