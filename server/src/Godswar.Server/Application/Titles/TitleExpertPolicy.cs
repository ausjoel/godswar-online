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
/// attribute buttons (1-8); a button is answered on page three with the granted ids (3000-3015, B22: 3016-3063) or
/// its requirement line (501/502 HP ... 535/536 absorption; B22: 7000 + class*100 + button*10 + tier) or 600.
///
/// Titles are claimed in order: the next unowned tier is checked, and every tier from it upward that
/// is met is granted in one click (the script concatenates the names). The stats compared are the
/// character's own calculated stats without altar or title bonuses ("Not including bonuses").
///
/// Retail gives titles no stat effect (IGG says nothing); the wiki's "title is also an increase in
/// characteristics" is GodsArena-only, so these titles are cosmetic. Only the Medusa titles keep their
/// existing stat bonus (MedusaTitleAttributePolicy), unchanged.
/// </remarks>
internal static partial class TitleExpertPolicy // B22: partial (LadderNames.cs)
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

    // B22 (item 12, the admin d39/d40/d41): six more tiers per attribute, per class, ids 3016-3063. Tier 8 is 90 % of
    // the class's computed maximum (batch-2026-11-21/D1 item12-title-ladder.tsv: MaxSim Lv200 best legal gear);
    // tiers 3-8 are 40/50/60/70/80/90 % (DEF Mage: geometric from tier 2, today's tiers are high for it).
    // Cosmetic like tiers 1-2. Names/descriptions are generated (client DesigName/DesigInfo lines from the
    // b22 title-ladder generator). A class with no row for a stat (ATK for Priest/Mage, MATK for
    // Warrior/Champion) keeps tiers 1-2 only and gets the "not for your class" line after them.
    public const uint LadderFirstTitle = 3016; // B22
    public const uint LadderLastTitle = 3063; // B22
    public const int LadderTiersPerAttribute = 6; // B22: tiers 3-8
    public const int LadderLineBase = 7000; // B22: 7000 + profession*100 + button*10 + tier (0 = not for class)

    /// <summary>B22: [button 1-8][profession 0-3] -> tier 3-8 thresholds, or null when the class has none.</summary>
    private static readonly int[]?[][] LadderThresholds =
    [
        // HP: Warrior, Champion, Priest, Mage
        [[86700, 108400, 130100, 151800, 173500, 195200], [87700, 109600, 131500, 153400, 175300, 197300],
         [67300, 84200, 101000, 117800, 134600, 151500], [59800, 74800, 89700, 104700, 119600, 134600]],
        // ATK: physical classes only
        [[6960, 8700, 10440, 12180, 13920, 15660], [8000, 10000, 12000, 14000, 16000, 18000], null, null],
        // MATK: magic classes only
        [null, null, [5650, 7060, 8480, 9890, 11300, 12710], [6030, 7530, 9040, 10550, 12050, 13560]],
        // DEF
        [[4430, 5540, 6650, 7750, 8860, 9970], [2900, 3620, 4340, 5070, 5790, 6520],
         [3200, 4010, 4810, 5610, 6410, 7210], [2940, 3320, 3750, 4240, 4790, 5420]],
        // MDEF
        [[3250, 4060, 4880, 5690, 6500, 7320], [2530, 3170, 3800, 4440, 5070, 5700],
         [3960, 4950, 5940, 6930, 7920, 8910], [3340, 4170, 5010, 5840, 6680, 7510]],
        // Hit
        [[930, 1170, 1400, 1630, 1870, 2100], [1360, 1700, 2040, 2380, 2720, 3060],
         [1040, 1300, 1560, 1820, 2080, 2340], [1250, 1570, 1880, 2190, 2510, 2820]],
        // Dodge
        [[920, 1150, 1380, 1610, 1840, 2060], [1130, 1420, 1700, 1990, 2270, 2550],
         [1240, 1550, 1860, 2170, 2480, 2790], [920, 1150, 1380, 1610, 1840, 2070]],
        // Absorb
        [[17800, 22200, 26600, 31100, 35500, 39900], [15000, 18700, 22500, 26200, 29900, 33700],
         [17500, 21800, 26200, 30500, 34900, 39300], [15300, 19100, 22900, 26700, 30600, 34400]]
    ];

    /// <summary>One step of an attribute ladder: threshold, title id and the line sent when it is not met.</summary>
    public readonly record struct AttributeTier(int Tier, int Need, uint TitleId, int RequirementLine); // B22

    /// <summary>B22: title id of tier 3-8 for a button (3016 + 6 per button).</summary>
    public static uint LadderTitleId(int button, int tier) =>
        (uint)(LadderFirstTitle + (button - 1) * LadderTiersPerAttribute + (tier - 3));

    /// <summary>B22: the line for "tier N not reached" (tier 3-8), or "not for your class" (tier 0).</summary>
    public static int LadderLine(byte profession, int button, int tier) =>
        LadderLineBase + profession * 100 + button * 10 + tier;

    /// <summary>B22: the full ladder (tiers 1-8, or 1-2 when the class has no row) for one button and class.</summary>
    public static IReadOnlyList<AttributeTier>? AttributeTiersFor(byte profession, int button)
    {
        if (AttributeFor(button) is not { } attribute)
        {
            return null;
        }

        var tiers = new List<AttributeTier>(8)
        {
            new(1, attribute.First, attribute.FirstTitle, attribute.FirstLine),
            new(2, attribute.Second, attribute.SecondTitle, attribute.SecondLine)
        };
        if (profession < 4 && LadderThresholds[button - 1][profession] is { } needs)
        {
            for (var index = 0; index < needs.Length; index++)
            {
                var tier = index + 3;
                tiers.Add(new(tier, needs[index], LadderTitleId(button, tier), LadderLine(profession, button, tier)));
            }
        }

        return tiers;
    }

    /// <summary>B22: true when the class has tiers 3-8 for this button.</summary>
    public static bool HasLadder(byte profession, int button) =>
        button >= 1 && button <= AttributeTitles.Length && profession < 4 &&
        LadderThresholds[button - 1][profession] is not null;

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

    // B22: per-class 8-tier ladder. Same claim rule as before: the next unowned tier is checked and every tier
    // from it upward that is met is granted in one click; the first unmet tier stops the claim.
    public static Claim ResolveAttributeClaim(
        byte profession, int button, TitleStats stats, IReadOnlyCollection<uint> owned)
    {
        if (AttributeTiersFor(profession, button) is not { } tiers)
        {
            return new([], [ErrorLine]);
        }

        var value = AttributeValue(button, stats);
        var grant = new List<uint>();
        foreach (var tier in tiers)
        {
            if (owned.Contains(tier.TitleId))
            {
                continue;
            }

            if (value >= tier.Need)
            {
                grant.Add(tier.TitleId);
                continue;
            }

            return grant.Count > 0
                ? new(grant, [.. grant.Select(static id => (int)id)])
                : new([], [tier.RequirementLine]);
        }

        if (grant.Count > 0)
        {
            return new(grant, [.. grant.Select(static id => (int)id)]);
        }

        // Every tier this class can earn is owned: the classes without a ladder for this stat hear why.
        return HasLadder(profession, button) || profession >= 4
            ? new([], [AllObtainedLine])
            : new([], [LadderLine(profession, button, 0)]);
    }

    /// <summary>
    /// B22: trims a claim so the character never owns more than <paramref name="limit"/> titles (the grants keep
    /// their order, the reply lists what is granted). Nothing fits: "Operation error" (500), nothing granted.
    /// </summary>
    public static Claim FitToTitleLimit(Claim claim, int ownedCount, int limit)
    {
        var room = limit - ownedCount;
        if (claim.Grant.Count <= room)
        {
            return claim;
        }

        if (room <= 0)
        {
            return new([], [ErrorLine]);
        }

        var grant = claim.Grant.Take(room).ToArray();
        return new(grant, [.. grant.Select(static id => (int)id)]);
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
