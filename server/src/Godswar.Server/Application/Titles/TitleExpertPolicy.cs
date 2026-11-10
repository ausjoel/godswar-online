using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Titles;

/// <summary>
/// TITLES: the Title Expert (Athens_071 / Sparta_071) and the quest titles.
/// </summary>
/// <remarks>
/// Rules: IGG guide "Title System" (Development/reference-analysis/text/igg-acid=50.txt), the
/// GodsArena wiki "Titles" page (wiki-65) for the quest list, and the client's own NPC script
/// <c>NpcFunDesidentify.lua</c> for every number sent back. The thresholds below are the IGG table;
/// they are the same numbers the client prints in NF_L0_DES13-28 and NF_L0_DES46-61.
///
/// Page one of function 12 is [1 = leveling title, 2 = attribute title]. Clicking 1 is answered on
/// page two with the granted title ids (1000-1303, the script appends each one's name) or one
/// requirement line (501-533) or 600 "all titles of this type". Clicking 2 opens page two's eight
/// attribute buttons (1-8); a button is answered on page three with the granted ids (3000-3015) or
/// its requirement line (501/502 HP ... 535/536 absorption) or 600.
///
/// Titles are claimed in order: the next unowned tier is checked, and every tier from it upward that
/// is met is granted in one click (the script concatenates the names). The stats compared are the
/// character's own calculated stats without altar or title bonuses ("Not including bonuses").
///
/// Retail gives titles no stat effect (IGG says nothing); the wiki's "title is also an increase in
/// characteristics" is GodsArena-only, so these titles are cosmetic. Only the Medusa titles keep their
/// existing stat bonus (MedusaTitleAttributePolicy), unchanged.
/// </remarks>
internal static class TitleExpertPolicy
{
    public const int IdentificationFunction = 12;
    public const int RewardFunction = 13;

    public const int LevelButton = 1;
    public const int AttributeButton = 2;

    /// <summary>NF_L0_DES12: "you have obtained all the titles of this type".</summary>
    public const int AllObtainedLine = 600;

    /// <summary>NF_L0_DES11: "Operation error! No such option!".</summary>
    public const int ErrorLine = 500;

    /// <summary>NpcFunDesaward page two: NF_L0_DES506 "This title type hasn't been released."</summary>
    public const int RewardNotReleasedLine = 600;

    public static readonly int[] AttributeButtons = [1, 2, 3, 4, 5, 6, 7, 8];

    public static readonly int[] RewardPageOneButtons = [1, 2, 3, 4, 5, 6];

    /// <summary>One leveling-title tier: level, attack, title id, requirement line.</summary>
    public readonly record struct LevelTier(int Level, int Attack, uint TitleId, int RequirementLine);

    /// <summary>Professions: 0 Warrior, 1 Champion, 2 Priest, 3 Mage (IGG table, client DES13-28).</summary>
    private static readonly LevelTier[][] LevelTiers =
    [
        [new(40, 250, 1000, 501), new(60, 600, 1001, 502), new(80, 1400, 1002, 503), new(100, 2400, 1003, 504)],
        [new(40, 300, 1100, 510), new(60, 800, 1101, 511), new(80, 1800, 1102, 512), new(100, 2800, 1103, 513)],
        [new(40, 180, 1200, 520), new(60, 400, 1201, 521), new(80, 900, 1202, 522), new(100, 1800, 1203, 523)],
        [new(40, 220, 1300, 530), new(60, 500, 1301, 531), new(80, 1100, 1302, 532), new(100, 2100, 1303, 533)]
    ];

    /// <summary>Warriors and Champions are judged by physical attack, Priests and Mages by magic attack.</summary>
    public static bool UsesMagicAttack(byte profession) => profession is 2 or 3;

    public static IReadOnlyList<LevelTier>? LevelTiersFor(byte profession) =>
        profession < LevelTiers.Length ? LevelTiers[profession] : null;

    /// <summary>The stats the Title Expert compares, without bonuses.</summary>
    public readonly record struct TitleStats(
        int Level, int MaxHp, int PhysicalAttack, int MagicAttack, int PhysicalDefense,
        int MagicDefense, int Hit, int Dodge, int DamageAbsorb);

    /// <summary>The outcome of one click: titles to grant (may be empty) and the numbers to send.</summary>
    public readonly record struct Claim(IReadOnlyList<uint> Grant, int[] Reply);

    public static Claim ResolveLevelClaim(byte profession, TitleStats stats, IReadOnlyCollection<uint> owned)
    {
        if (LevelTiersFor(profession) is not { } tiers)
        {
            return new([], [ErrorLine]);
        }

        var attack = UsesMagicAttack(profession) ? stats.MagicAttack : stats.PhysicalAttack;
        var grant = new List<uint>();
        foreach (var tier in tiers)
        {
            if (owned.Contains(tier.TitleId))
            {
                continue;
            }

            if (stats.Level >= tier.Level && attack >= tier.Attack)
            {
                grant.Add(tier.TitleId);
                continue;
            }

            // The first tier not met stops the claim: titles come in order.
            return grant.Count > 0
                ? new(grant, [.. grant.Select(static id => (int)id)])
                : new([], [tier.RequirementLine]);
        }

        return grant.Count > 0
            ? new(grant, [.. grant.Select(static id => (int)id)])
            : new([], [AllObtainedLine]);
    }

    /// <summary>One attribute: the two thresholds, their titles and requirement lines.</summary>
    public readonly record struct AttributeTitle(
        string Name, int First, int Second, uint FirstTitle, uint SecondTitle, int FirstLine, int SecondLine);

    /// <summary>Page-two buttons 1-8 in the script's order (NF_L0_DES3-10).</summary>
    private static readonly AttributeTitle[] AttributeTitles =
    [
        new("HP", 20000, 32000, 3000, 3001, 501, 502),
        new("ATK", 3600, 4500, 3002, 3003, 505, 506),
        new("MATK", 2800, 3500, 3004, 3005, 510, 511),
        new("DEF", 2000, 2600, 3006, 3007, 515, 516),
        new("MDEF", 1300, 1800, 3008, 3009, 520, 521),
        new("Hit", 500, 650, 3010, 3011, 525, 526),
        new("Dodge", 500, 650, 3012, 3013, 530, 531),
        new("Absorb", 900, 1300, 3014, 3015, 535, 536)
    ];

    public static AttributeTitle? AttributeFor(int button) =>
        button >= 1 && button <= AttributeTitles.Length ? AttributeTitles[button - 1] : null;

    public static int AttributeValue(int button, TitleStats stats) => button switch
    {
        1 => stats.MaxHp,
        2 => stats.PhysicalAttack,
        3 => stats.MagicAttack,
        4 => stats.PhysicalDefense,
        5 => stats.MagicDefense,
        6 => stats.Hit,
        7 => stats.Dodge,
        8 => stats.DamageAbsorb,
        _ => 0
    };

    public static Claim ResolveAttributeClaim(int button, TitleStats stats, IReadOnlyCollection<uint> owned)
    {
        if (AttributeFor(button) is not { } attribute)
        {
            return new([], [ErrorLine]);
        }

        var value = AttributeValue(button, stats);
        var grant = new List<uint>();
        if (!owned.Contains(attribute.FirstTitle))
        {
            if (value < attribute.First)
            {
                return new([], [attribute.FirstLine]);
            }

            grant.Add(attribute.FirstTitle);
        }

        if (!owned.Contains(attribute.SecondTitle))
        {
            if (value >= attribute.Second)
            {
                grant.Add(attribute.SecondTitle);
            }
            else if (grant.Count == 0)
            {
                return new([], [attribute.SecondLine]);
            }
        }

        return grant.Count > 0
            ? new(grant, [.. grant.Select(static id => (int)id)])
            : new([], [AllObtainedLine]);
    }

    /// <summary>
    /// Quest id -> title id. The IGG quest list ("Commission", "List As Wanted 1", "Hero's Trial 1/5")
    /// names quests that do not exist in this client (Text/Quest/*.dat), so the wiki-65 list is used,
    /// keeping the two quests both sources agree on (List As Wanted 3 / 7). Title ids are the client's
    /// DesigName.dat entries; Greek Vanguard has only one id (2506), used for both factions. The level 9
    /// and 25 quests of the wiki list (12/1012, 116/1116) are not in the server's quest chain, so the
    /// Soldier and Exterminator titles (2000, 2001, 2500, 2501) cannot be earned yet.
    /// </summary>
    public static readonly IReadOnlyDictionary<uint, uint> QuestTitles = new Dictionary<uint, uint>
    {
        // Athens
        [1012] = 2000, // Zander's Snakes -> Athenian Soldier (quest not in the chain)
        [1116] = 2001, // Vicious Queen Nina -> Exterminator (quest not in the chain)
        [1118] = 2002, // List As Wanted 3 (Lv56) -> Boar Killer
        [1217] = 2003, // 3 Warlords: Soulless (Lv75) -> Budding Hero
        [1221] = 2004, // 3 Warlords: Beast Master (Lv95) -> Athenian Warrior
        [1122] = 2005, // List As Wanted 7 (Lv100) -> Bounty Hunter
        [1376] = 2506, // Death of a Captain (Lv120) -> Greek Vanguard
        // Sparta
        [12] = 2500, // Foka's Chests -> Spartan Soldier (quest not in the chain)
        [116] = 2501, // Gordon, the Brute -> Exterminator (quest not in the chain)
        [118] = 2502, // Bounty Hunter: the Astrien (Lv56) -> Wolf Killer
        [217] = 2503, // 3 Warlords: Soulless (Lv75) -> Budding Hero
        [221] = 2504, // 3 Warlords: Beast Master (Lv95) -> Spartan Warrior
        [122] = 2505, // Centaur Shaikh Hailer (Lv100) -> Bounty Hunter
        [376] = 2506 // Death of a Captain (Lv120) -> Greek Vanguard
    };

    /// <summary>Quest titles the completed quests earn and the character does not own yet.</summary>
    public static IReadOnlyList<uint> MissingQuestTitles(
        IEnumerable<uint> completedQuestIds, IReadOnlyCollection<uint> owned)
    {
        var missing = new List<uint>();
        foreach (var questId in completedQuestIds)
        {
            if (QuestTitles.TryGetValue(questId, out var titleId) &&
                !owned.Contains(titleId) && !missing.Contains(titleId))
            {
                missing.Add(titleId);
            }
        }

        return missing;
    }

    /// <summary>
    /// Off by default: the server rate <c>npc.title_expert</c> = 1 or env
    /// <c>GODSWAR_TITLE_EXPERT=true</c> (or 1) turns the Title Expert and quest titles on.
    /// </summary>
    public static bool IsEnabled() =>
        IsEnvOn("GODSWAR_TITLE_EXPERT") ||
        ServerRates.Current.Get(ServerRateKeys.TitleExpertEnabled) >= 1m;

    internal static bool IsEnvOn(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
}
