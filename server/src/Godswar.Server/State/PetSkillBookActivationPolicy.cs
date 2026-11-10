using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

internal sealed record PetSkillBookActivationDefinition(
    uint ItemId,
    int FamilyType,
    short Priority,
    int RuntimeSkillId,
    int RestrictedSpeciesId,
    PetSkillTraitRequirement TraitRequirement);

/// <summary>
/// Fail-closed allow-list for the stock pet-skill books reviewed for live
/// activation. Item metadata and learned-skill content must agree with this
/// independent mapping before a book can reach durable mutation code.
/// </summary>
internal static partial class PetSkillBookActivationPolicy
{
    /// <summary>
    /// Every distinct stock book is reviewed, so the allow-list sizes are
    /// invariants of the installed client rather than a subset selection. The
    /// client repeats twelve exact item rows (10600-10605 and 10610-10615), so
    /// its 402 book rows collapse to 390 distinct item IDs.
    /// </summary>
    public const int ReviewedBookCount = 390;
    public const int SpeciesExclusiveFamilyCount = 44;

    static PetSkillBookActivationPolicy()
    {
        if (ReviewedBooks.Count != ReviewedBookCount ||
            SpeciesExclusiveFamilies.Count != SpeciesExclusiveFamilyCount)
        {
            throw new InvalidDataException(
                "The installed-client pet skill-book catalog is incomplete.");
        }
    }

    public static bool IsReviewedItem(uint itemId) =>
        ReviewedBooks.ContainsKey(itemId);

    /// <summary>
    /// Species that exclusively owns a skill family, or zero when every pet
    /// may learn it.
    /// </summary>
    public static int ResolveRestrictedSpeciesId(int familyType) =>
        SpeciesExclusiveFamilies.TryGetValue(familyType, out var speciesId)
            ? speciesId
            : 0;

    /// <summary>
    /// Whether the carried pet may consume this book. A restricted family is
    /// open to its owning species and to any pet that was born with that
    /// family as its species starter skill, so an innate skill can always be
    /// advanced. The pet's current species decides, and a species change never
    /// invalidates a family the pet already learned.
    /// </summary>
    public static bool CanSpeciesLearn(
        int petSpeciesId,
        int petInnateFamilyType,
        PetSkillBookActivationDefinition book) =>
        book.RestrictedSpeciesId == 0 ||
        book.RestrictedSpeciesId == petSpeciesId ||
        book.FamilyType == petInnateFamilyType;

    public static bool TryResolve(
        IItemTemplateCatalog items,
        IPetLearnedSkillContentCatalog learnedSkills,
        uint itemId,
        out PetSkillBookActivationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(learnedSkills);
        definition = null!;
        if (!ReviewedBooks.TryGetValue(itemId, out var reviewed) ||
            !items.TryGet(itemId, out var item) ||
            !string.Equals(
                item.Kind,
                "consume item",
                StringComparison.Ordinal) ||
            !TryReadReviewedMetadata(
                item.StatsJson,
                checked((int)itemId),
                reviewed.RuntimeSkillId,
                reviewed.Priority) ||
            !learnedSkills.TryGetCurveByRuntimeSkillId(
                reviewed.RuntimeSkillId,
                out var curve) ||
            curve.FirstRuntimeSkillId != reviewed.RuntimeSkillId ||
            curve.Priority != reviewed.Priority)
        {
            return false;
        }

        definition = new(
            itemId,
            curve.FamilyType,
            curve.Priority,
            curve.FirstRuntimeSkillId,
            ResolveRestrictedSpeciesId(curve.FamilyType),
            curve.LearnTraitRequirement);
        return true;
    }

    private static bool TryReadReviewedMetadata(
        string statsJson,
        int expectedItemId,
        int expectedSkillId,
        short expectedPriority)
    {
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            var root = document.RootElement;
            if (!TryReadInt32(root, "ID", out var id) ||
                !TryReadInt32(root, "Use", out var use) ||
                !TryReadInt32(root, "Overlap", out var overlap) ||
                !TryReadInt32(root, "ItemType", out var itemType) ||
                !TryReadInt32(root, "PetSkill", out var skillId) ||
                id != expectedItemId || use != 1 || overlap != 99 ||
                skillId != expectedSkillId)
            {
                return false;
            }

            return itemType == (expectedPriority == 1 ? 4 : 3);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadInt32(
        JsonElement root,
        string property,
        out int value)
    {
        value = 0;
        if (!root.TryGetProperty(property, out var element))
        {
            return false;
        }
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }
}
