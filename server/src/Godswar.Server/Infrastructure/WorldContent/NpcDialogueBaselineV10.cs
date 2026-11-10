using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Corrects the two Level Sealer descriptions for the all-level service while
/// preserving the complete V9 route, profile, and menu geometry.
/// </summary>
internal static class NpcDialogueBaselineV10
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV9.ExpectedTextCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV9.ExpectedProfileCount;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV9.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV9.ExpectedMenuEntryCount;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV9.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "0B7A165E5E3ED284EEF3A5568E82EF573F808514FB89AF345BFFAB7574A6A4F3";
    public const string Source = "reviewed-published-npc-dialogue-v10";
    public const string LevelSealerDescription =
        "I can help you seal your level — there are no restrictions; " +
        "any player at any level can use this service.";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV9.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV9.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV9.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var overrideCount = 0;
        var result = texts.Select(text =>
        {
            if (text.NpcKey is not ("Athens_142" or "Sparta_142"))
            {
                return text;
            }

            overrideCount++;
            return text with { Description = LevelSealerDescription };
        }).ToArray();
        if (overrideCount != 2)
        {
            throw new InvalidDataException(
                "The reviewed Level Sealer text overlay requires both " +
                "capital NPCs exactly once.");
        }

        return result;
    }
}
