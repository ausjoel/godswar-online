using Godswar.Server.Application.World;

namespace Godswar.Server.State;

/// <summary>
/// Picks the skill book a wish hands out.
/// </summary>
/// <remarks>
/// The class comes from the button the player clicked, not from the character:
/// the client's own class buttons are 301 Warrior, 401 Champion, 501 Mage and 601
/// Priest, so the click is authoritative for which class of book is drawn.
/// <para>
/// The draw picks a <b>tier</b> first and a level inside that tier second.
/// The ordinary tier (levels 1-2) is the 98% case; the advanced tier is the 2%
/// case and stops at level 4, so level 5 is never handed out.
/// </para>
/// </remarks>
internal static class WishingPoolCatalog
{
    /// <summary>
    /// The highest skill level the advanced tier may hand out.
    /// </summary>
    public const int MaximumWishSkillLevel = 4;

    /// <summary>
    /// The advanced tier's share of the draw, as parts per ten thousand.
    /// </summary>
    public const int AdvancedTierWeight = 200;

    /// <summary>
    /// The ordinary tier's share of the draw, as parts per ten thousand.
    /// </summary>
    public const int OrdinaryTierWeight = 9800;

    private const int TierWeightTotal =
        AdvancedTierWeight + OrdinaryTierWeight;

    /// <summary>
    /// The levels inside the ordinary tier, as (skill level, weight).
    /// </summary>
    private static readonly (int SkillLevel, int Weight)[] OrdinaryLevelWeights =
    [
        (1, 70),
        (2, 30)
    ];

    /// <summary>
    /// The levels inside the advanced tier, as (skill level, weight).
    /// </summary>
    private static readonly (int SkillLevel, int Weight)[] AdvancedLevelWeights =
    [
        (3, 60),
        (4, 40)
    ];

    /// <summary>
    /// Maps the client's class button to the catalog's class id. The catalog's own
    /// class list is warror 0, champion 1, <b>priest 2</b>, <b>mage 3</b>
    /// (<c>gameplay_class_definitions</c>), which is why the mage button maps to 3
    /// and the priest button to 2.
    /// </summary>
    public static bool TryResolveClassButton(int subId, out byte characterClass)
    {
        characterClass = subId switch
        {
            301 => 0,
            401 => 1,
            501 => 3,
            601 => 2,
            _ => byte.MaxValue
        };
        return characterClass != byte.MaxValue;
    }

    /// <summary>
    /// Draws the skill level for one wish: ordinary (1-2) in 98% of wishes,
    /// advanced (3-4) in the remaining 2%.
    /// </summary>
    public static int DrawSkillLevel()
    {
        var roll = Random.Shared.Next(TierWeightTotal);
        return roll < AdvancedTierWeight
            ? PickWeighted(AdvancedLevelWeights)
            : PickWeighted(OrdinaryLevelWeights);
    }

    private static int PickWeighted((int SkillLevel, int Weight)[] table)
    {
        var total = 0;
        foreach (var (_, weight) in table)
        {
            total += weight;
        }

        var roll = Random.Shared.Next(total);
        foreach (var (skillLevel, weight) in table)
        {
            if (roll < weight)
            {
                return skillLevel;
            }

            roll -= weight;
        }

        return table[^1].SkillLevel;
    }

    /// <summary>
    /// Whether a drawn skill level belongs to the advanced tier. Only the
    /// advanced tier is announced realm-wide.
    /// </summary>
    public static bool IsAdvancedTier(int skillLevel) =>
        skillLevel >= AdvancedLevelWeights[0].SkillLevel;

    public static IReadOnlyList<GameplaySkillBookDefinition> Resolve(
        IReadOnlyList<GameplaySkillBookDefinition> books,
        byte characterClass,
        int skillLevel)
    {
        ArgumentNullException.ThrowIfNull(books);
        var matching = new List<GameplaySkillBookDefinition>();
        foreach (var book in books)
        {
            if (book.SkillLevel != skillLevel ||
                !book.ClassIds.Contains(characterClass))
            {
                continue;
            }

            matching.Add(book);
        }

        return matching;
    }
}
