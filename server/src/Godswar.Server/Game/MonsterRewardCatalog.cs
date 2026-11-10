using Godswar.Server.Application.Rewards;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static class MonsterRewardCatalog
{
    private const int CapturedNormalMonsterMultiplier = 4;
    internal const int NormalTalentExperience = 10;

    // Original server MonsterEXP table. Captured normal Sparta monsters use a
    // template multiplier of four: tier 1 therefore awards 20 * 4 = 80 EXP.
    private static readonly int[] MonsterExperience =
    [
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 32, 33, 34, 35, 36, 37, 38, 39, 40,
        41, 42, 43, 44, 45, 47, 48, 49, 50, 51,
        52, 53, 54, 56, 57, 58, 59, 60, 61, 62,
        64, 65, 66, 67, 68, 69, 71, 72, 73, 74,
        75, 77, 78, 79, 80, 81, 83, 84, 85, 86,
        88, 89, 90, 91, 93, 94, 95, 96, 98, 99,
        100, 101, 103, 104, 105, 106, 108, 109, 110, 111,
        113, 114, 115, 117, 118, 119, 120, 122, 123, 124,
        126, 127, 128, 130, 131, 132, 134, 135, 136, 137,
        139, 140, 141, 143, 144, 145, 147, 148, 149, 151,
        152, 154, 155, 156, 158, 159, 160, 162, 163, 164,
        166, 167, 168, 170, 171, 173, 174, 175, 177, 178,
        179, 181, 182, 184, 185, 186, 188, 189, 191, 192,
        193, 195, 196, 198, 199, 200, 202, 203, 205, 206,
        208, 209, 210, 212, 213, 215, 216, 217, 219, 220,
        222, 223, 225, 226, 228, 229, 230, 232, 233, 235,
        236, 238, 239, 241, 242, 243, 245, 246, 248, 249,
        251, 252, 254, 255, 257, 258, 260, 261, 263, 264,
        265, 267, 268, 270, 271, 273, 274, 276, 277, 279
    ];

    public static MonsterKillReward Resolve(
        MonsterRuntimeSnapshot monster,
        int playerLevel,
        MonsterRewardPolicySnapshot policy) =>
        Resolve(monster.Definition.Tier, playerLevel, policy);

    public static bool IsEligible(
        MonsterRuntimeSnapshot monster,
        int playerLevel,
        MonsterRewardPolicySnapshot policy) =>
        IsEligible(monster.Definition.Tier, playerLevel, policy);

    public static int ResolvePetExperience(
        MonsterRuntimeSnapshot monster,
        int playerLevel,
        MonsterRewardPolicySnapshot policy) =>
        ResolvePetExperience(
            monster.Definition.Tier,
            playerLevel,
            policy);

    /// <summary>
    /// PARTY (r02): the stock kill reward before any level rule, so a party
    /// split does not depend on which member landed the claim.
    /// </summary>
    public static MonsterKillReward ResolveStockReward(
        MonsterRuntimeSnapshot monster) =>
        new(
            BaseExperience(NormalizeTier(monster.Definition.Tier)),
            NormalTalentExperience);

    /// <summary>
    /// PARTY (r02): whether a character of this level earns EXP from this
    /// monster at all (below the level cap and inside the level-gap rule).
    /// </summary>
    public static bool EarnsExperience(
        MonsterRuntimeSnapshot monster,
        int playerLevel,
        MonsterRewardPolicySnapshot policy) =>
        playerLevel < PlayerExperienceCatalog.MaximumLevel &&
        IsEligible(monster, playerLevel, policy);

    /// <summary>
    /// PARTY (r02): one equal share. Rounds down (the remainder is not paid);
    /// a positive amount never rounds to nothing.
    /// </summary>
    public static int SplitEqually(int amount, int divisor) =>
        amount <= 0 || divisor <= 1
            ? Math.Max(0, amount)
            : Math.Max(1, amount / divisor);

    internal static MonsterKillReward Resolve(uint monsterTier, int playerLevel)
        => Resolve(
            monsterTier,
            playerLevel,
            MonsterRewardPolicySnapshot.Default);

    internal static MonsterKillReward Resolve(
        uint monsterTier,
        int playerLevel,
        MonsterRewardPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (playerLevel >= PlayerExperienceCatalog.MaximumLevel)
        {
            return new MonsterKillReward(0, 0);
        }

        var tier = NormalizeTier(monsterTier);
        playerLevel = Math.Max(1, playerLevel);
        if (!policy.IsEligible(playerLevel, tier))
        {
            return new MonsterKillReward(0, 0);
        }

        return new MonsterKillReward(
            BaseExperience(tier),
            NormalTalentExperience);
    }

    internal static bool IsEligible(
        uint monsterTier,
        int playerLevel,
        MonsterRewardPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.IsEligible(playerLevel, NormalizeTier(monsterTier));
    }

    internal static int ResolvePetExperience(
        uint monsterTier,
        int playerLevel,
        MonsterRewardPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var tier = NormalizeTier(monsterTier);
        return policy.IsEligible(playerLevel, tier)
            ? BaseExperience(tier)
            : 0;
    }

    private static int NormalizeTier(uint monsterTier) =>
        Math.Clamp(
            (int)Math.Min(monsterTier, int.MaxValue),
            1,
            MonsterExperience.Length);

    private static int BaseExperience(int tier) =>
        MonsterExperience[tier - 1] * CapturedNormalMonsterMultiplier;
}

internal readonly record struct MonsterKillReward(
    int Experience,
    int TalentExperience);
