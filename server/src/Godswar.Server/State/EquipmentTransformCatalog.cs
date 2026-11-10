using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// Level 3 Amethyst equipment transform ("Trans" tab of the forge window).
/// </summary>
/// <remarks>
/// Client evidence (Origin.exe, same build as GodsWar.exe in GodsWar.app):
/// the preview window (0x5ea8f0) lists every RltID entry of the source
/// item's EquipForge.xml row except the source itself; the gear slot accepts
/// any equipment, the second slot only a BijouForge MaterialType 7 item
/// (4252, Level 3 Amethyst). The client never sends which preview entry was
/// clicked (0x5eb02e sends the source id, 0 and slot 2), so the server picks
/// the result from the same list. The S2C 10112 reply (0x5614c0) rewrites the
/// staged gear to the returned id, keeps quality, grade, attributes and
/// sockets, binds it when the amethyst was bound, and spends one amethyst.
/// </remarks>
internal static partial class EquipmentTransformCatalog
{
    public const uint LevelThreeAmethystItemId = 4252;

    public static bool IsTransformable(uint sourceItemId) =>
        Targets.ContainsKey(sourceItemId);

    /// <summary>
    /// The transform targets that exist in the published item content.
    /// </summary>
    public static IReadOnlyList<uint> GetPublishedTargets(
        uint sourceItemId,
        IItemTemplateCatalog templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (!Targets.TryGetValue(sourceItemId, out var targets))
        {
            return [];
        }

        return targets
            .Where(target => target != sourceItemId &&
                templates.TryGet(target, out _))
            .ToArray();
    }

    /// <summary>The planned bag mutation; null when the request is invalid.</summary>
    public static AmethystTransformPlan? Plan(
        CompactItemEntry gear,
        CompactItemEntry amethyst,
        IReadOnlyList<uint> publishedTargets,
        int randomIndex)
    {
        if (gear.IsEmpty ||
            amethyst.IsEmpty ||
            amethyst.Id != LevelThreeAmethystItemId ||
            amethyst.Stack < 1 ||
            publishedTargets.Count == 0 ||
            randomIndex < 0 ||
            randomIndex >= publishedTargets.Count)
        {
            return null;
        }

        var target = publishedTargets[randomIndex];
        var bound = amethyst.Bound > 0 ? (short)Math.Max((short)1, gear.Bound) : gear.Bound;
        var gearAfter = gear with { Id = target, Bound = bound };
        var amethystAfter = amethyst.Stack > 1
            ? amethyst with { Stack = (short)(amethyst.Stack - 1) }
            : CompactItemEntry.Empty;
        return new AmethystTransformPlan(gearAfter, amethystAfter);
    }
}

internal sealed record AmethystTransformPlan(
    CompactItemEntry GearAfter,
    CompactItemEntry AmethystAfter);

internal enum AmethystTransformStatus
{
    Transformed,
    CharacterNotFound,
    StaleSelection,
    NotTransformable,
    NoAmethyst
}

internal sealed record AmethystTransformResult(
    AmethystTransformStatus Status,
    GameCharacter? Character,
    CompactItemEntry GearBefore,
    CompactItemEntry GearAfter);
