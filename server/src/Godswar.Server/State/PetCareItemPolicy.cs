using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// What a reviewed stock pet-care consumable does to the summoned pet.
/// Satiety, amity, lifetime, and energy all come from the installed client's
/// ItemBaseAttribute rows: <c>Fill</c>/<c>Favor</c> for food and wine,
/// <c>ItemType="8" Values="100"</c> for the lifetime spring, and
/// <c>ItemType="14" Values="360|810|1710"</c> for the three energy waters on
/// the client's fixed 0..1800 energy scale.
/// </summary>
internal enum PetCareItemEffectKind
{
    /// <summary>Food. Raises satiety and amity; a mismatched food kind
    /// raises only half the satiety and never raises amity.</summary>
    Food = 0,

    /// <summary>Amity only. The two wines carry no satiety at all.</summary>
    Amity = 1,

    /// <summary>Remaining lifetime (仙宠泉水, +100).</summary>
    Lifetime = 2,

    /// <summary>Merge energy, granted as a fraction of the pet's own
    /// maximum.</summary>
    Energy = 3
}

/// <summary>
/// One reviewed pet-care consumable resolved from the published item content.
/// <paramref name="FoodKind"/> is the food type the item belongs to (1 grass,
/// 2 meat, 3 meal) and is zero for the wines and the waters.
/// <paramref name="EnergyNumerator"/>/<paramref name="EnergyDenominator"/>
/// carry the client's native energy grant (360/810/1710 out of 1800) as an
/// exact fraction of the pet's maximum energy so durable state stays on its
/// own normalized scale.
/// </summary>
internal readonly record struct PetCareItemDefinition(
    uint ItemId,
    PetCareItemEffectKind Kind,
    int Satiety,
    int Amity,
    int Lifetime,
    short FoodKind,
    int EnergyNumerator,
    int EnergyDenominator);

/// <summary>
/// Resolves the stock pet-care consumables the installed client ships. Every
/// number here is the client's own: the four food grades of each food kind,
/// the two wines, the lifetime spring, and the three merge-energy waters.
/// Item IDs identify the family; the published revision still owns the
/// metadata and any disagreement fails closed.
/// </summary>
internal static class PetCareItemPolicy
{
    public const int GrassFoodKind = 1;
    public const int MeatFoodKind = 2;
    public const int MealFoodKind = 3;

    /// <summary>The client's fixed 0..1800 energy presentation scale.</summary>
    public const int NativeEnergyScale = 1_800;

    public const int MismatchedFoodSatietyDivisor = 2;

    /// <summary>
    /// True for every ID this policy has a client-confirmed rule for. Callers
    /// must still use <see cref="TryResolvePublished"/>: a handful of these
    /// rows are not in the pinned publication yet, and a reviewed item that
    /// was never published must be left alone rather than turned into a
    /// startup or dispatch exception.
    /// </summary>
    public static bool IsReviewedItem(uint itemId) =>
        (itemId is >= FirstFoodItemId and <= LastFoodItemId &&
            itemId % 10 is >= 0 and <= 3) ||
        itemId is MellowWineItemId or
            FineWineItemId or
            LifetimeSpringItemId or
            EnergySpringItemId or
            EnergyBlessingWaterItemId or
            EnergyHolyWaterItemId;

    /// <summary>
    /// Resolves a reviewed pet-care item from the pinned catalog, returning
    /// false when the item is not published at all. Only a published item
    /// whose metadata actually disagrees with the client throws.
    /// </summary>
    public static bool TryResolvePublished(
        IItemTemplateCatalog templates,
        uint itemId,
        out PetCareItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(templates);
        definition = default;
        if (!IsReviewedItem(itemId) || !templates.TryGet(itemId, out _))
        {
            return false;
        }
        return TryResolve(templates, itemId, out definition);
    }

    public const uint FirstFoodItemId = 10_000;
    public const uint LastFoodItemId = 10_043;

    public const uint MellowWineItemId = 10_060;
    public const uint FineWineItemId = 10_061;
    public const uint LifetimeSpringItemId = 10_090;
    public const uint EnergySpringItemId = 4_060;
    public const uint EnergyBlessingWaterItemId = 4_061;
    public const uint EnergyHolyWaterItemId = 4_062;

    private static readonly uint[] ReviewedItemIds =
    [
        10_000, 10_001, 10_002, 10_003,
        10_020, 10_021, 10_022, 10_023,
        10_040, 10_041, 10_042, 10_043,
        10_060, 10_061, 10_090,
        4_060, 4_061, 4_062
    ];

    public static IReadOnlyList<uint> Items => ReviewedItemIds;

    public static bool TryResolve(
        IItemTemplateCatalog templates,
        uint itemId,
        out PetCareItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(templates);
        definition = default;
        if (!IsReviewedItem(itemId))
        {
            return false;
        }
        if (!templates.TryGet(itemId, out var template))
        {
            throw new InvalidDataException(
                $"Pet-care item {itemId} is absent from official content.");
        }

        using var document = JsonDocument.Parse(template.StatsJson);
        var stats = document.RootElement;
        if (template.Id != itemId ||
            !string.Equals(template.Kind, "consume item", StringComparison.Ordinal) ||
            !ReadRequired(stats, "ID").Equals(
                itemId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            ReadRequired(stats, "Use") != "1")
        {
            throw new InvalidDataException(
                $"Pet-care item {itemId} has invalid official metadata.");
        }

        definition = itemId switch
        {
            MellowWineItemId => ReadAmityOnly(itemId, stats, 100),
            FineWineItemId => ReadAmityOnly(itemId, stats, 20),
            LifetimeSpringItemId => ReadLifetimeSpring(itemId, stats),
            EnergySpringItemId => ReadEnergyWater(itemId, stats, 360),
            EnergyBlessingWaterItemId => ReadEnergyWater(itemId, stats, 810),
            EnergyHolyWaterItemId => ReadEnergyWater(itemId, stats, 1710),
            _ => ReadFood(itemId, stats)
        };
        return true;
    }

    /// <summary>
    /// Satiety for a food of <paramref name="itemFoodKind"/> fed to a pet
    /// whose own food kind is <paramref name="petFoodKind"/>. The client's
    /// tooltip (PETDP_X0_19) is explicit: the wrong type adds only half the
    /// satiety and never raises amity.
    /// </summary>
    public static int ResolveSatiety(
        PetCareItemDefinition definition,
        short petFoodKind)
    {
        if (definition.Kind != PetCareItemEffectKind.Food)
        {
            return definition.Satiety;
        }
        return definition.FoodKind == petFoodKind
            ? definition.Satiety
            : definition.Satiety / MismatchedFoodSatietyDivisor;
    }

    /// <summary>
    /// Amity for the same pairing. Zero whenever the food type disagrees.
    /// </summary>
    public static int ResolveAmity(
        PetCareItemDefinition definition,
        short petFoodKind)
    {
        if (definition.Kind != PetCareItemEffectKind.Food)
        {
            return definition.Amity;
        }
        return definition.FoodKind == petFoodKind ? definition.Amity : 0;
    }

    /// <summary>
    /// Merge energy granted to a pet whose maximum energy is
    /// <paramref name="maximumEnergy"/>, rounded down and clamped to the
    /// remainder so the caller can add it without overflowing the cap.
    /// </summary>
    public static int ResolveEnergyGrant(
        PetCareItemDefinition definition,
        int maximumEnergy)
    {
        if (definition.Kind != PetCareItemEffectKind.Energy)
        {
            return 0;
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEnergy);
        return checked((int)(
            (long)maximumEnergy * definition.EnergyNumerator /
            definition.EnergyDenominator));
    }

    private static PetCareItemDefinition ReadAmityOnly(
        uint itemId,
        JsonElement stats,
        int amity) =>
        ReadRequired(stats, "ItemType") == "7" &&
        ReadRequired(stats, "Food") == "0" &&
        ReadRequired(stats, "Fill") == "0" &&
        ReadInt(stats, "Favor") == amity
            ? new(
                itemId,
                PetCareItemEffectKind.Amity,
                Satiety: 0,
                Amity: amity,
                Lifetime: 0,
                FoodKind: 0,
                EnergyNumerator: 0,
                EnergyDenominator: 0)
            : throw new InvalidDataException(
                $"Pet-care wine {itemId} has invalid official metadata.");

    private static PetCareItemDefinition ReadLifetimeSpring(
        uint itemId,
        JsonElement stats) =>
        ReadRequired(stats, "ItemType") == "8" &&
        ReadInt(stats, "Values") == 100
            ? new(
                itemId,
                PetCareItemEffectKind.Lifetime,
                Satiety: 0,
                Amity: 0,
                Lifetime: 100,
                FoodKind: 0,
                EnergyNumerator: 0,
                EnergyDenominator: 0)
            : throw new InvalidDataException(
                $"Pet-care lifetime spring {itemId} has invalid metadata.");

    private static PetCareItemDefinition ReadEnergyWater(
        uint itemId,
        JsonElement stats,
        int nativeEnergy) =>
        ReadRequired(stats, "ItemType") == "14" &&
        ReadInt(stats, "Values") == nativeEnergy
            ? new(
                itemId,
                PetCareItemEffectKind.Energy,
                Satiety: 0,
                Amity: 0,
                Lifetime: 0,
                FoodKind: 0,
                EnergyNumerator: nativeEnergy,
                EnergyDenominator: NativeEnergyScale)
            : throw new InvalidDataException(
                $"Pet-care energy water {itemId} has invalid metadata.");

    private static PetCareItemDefinition ReadFood(
        uint itemId,
        JsonElement stats)
    {
        var foodKind = ReadInt(stats, "Food");
        var satiety = ReadInt(stats, "Fill");
        var amity = ReadInt(stats, "Favor");
        if (foodKind is not (
                GrassFoodKind or MeatFoodKind or MealFoodKind) ||
            ReadRequired(stats, "ItemType") != "7" ||
            satiety <= 0 ||
            amity < 0)
        {
            throw new InvalidDataException(
                $"Pet-care food {itemId} has invalid official metadata.");
        }
        return new(
            itemId,
            PetCareItemEffectKind.Food,
            satiety,
            amity,
            Lifetime: 0,
            checked((short)foodKind),
            EnergyNumerator: 0,
            EnergyDenominator: 0);
    }

    private static int ReadInt(JsonElement stats, string name) =>
        int.TryParse(
            ReadRequired(stats, name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : throw new InvalidDataException(
                $"Pet-care content has a non-numeric {name}.");

    private static string ReadRequired(JsonElement stats, string name) =>
        stats.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException(
                $"Pet-care content is missing {name}.");
}
