using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Removes the Atlantis entry level ceiling while retaining every V22 route,
/// other description, and immutable spawn dependency.
/// </summary>
internal static class NpcDialogueBaselineV23
{
    public const int ExpectedTextCount = NpcDialogueBaselineV22.ExpectedTextCount;
    public const int ExpectedProfileCount = NpcDialogueBaselineV22.ExpectedProfileCount;
    public const int ExpectedRouteCount = NpcDialogueBaselineV22.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount = NpcDialogueBaselineV22.ExpectedMenuEntryCount;
    public const int ExpectedHashedEntryCount = ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision = NpcDialogueBaselineV22.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "3485F0BC203A282720A9A1C11DEB5288FF1A044F17E684DD3D8C9D51B749B240";
    public const string Source = "reviewed-published-npc-dialogue-v23";
    public static string InstanceCallerDescription { get; } =
        NpcDialogueBaselineV22.InstanceCallerDescription.Replace(
            "Level 90-140", "Level 90+", StringComparison.Ordinal);

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV22.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV22.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV22.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts)
    {
        var inherited = NpcDialogueBaselineV22.ApplyTextOverrides(texts);
        var overrideCount = 0;
        var result = inherited.Select(text =>
        {
            if (text.NpcKey is not ("Athens_060" or "Sparta_060"))
            {
                return text;
            }

            overrideCount++;
            return text with { Description = InstanceCallerDescription };
        }).ToArray();
        if (overrideCount != 2)
        {
            throw new InvalidDataException(
                "The V23 Instance Caller text overlay requires both " +
                "capital callers exactly once.");
        }

        return result;
    }
}
