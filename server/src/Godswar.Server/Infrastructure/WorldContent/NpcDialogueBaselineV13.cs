using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Corrects the two capital Instance Caller descriptions for the three-entry
/// Atlantis and Wonderland policy while preserving the complete V12 dialogue
/// geometry.
/// </summary>
internal static class NpcDialogueBaselineV13
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV12.ExpectedTextCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV12.ExpectedProfileCount;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV12.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV12.ExpectedMenuEntryCount;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV12.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "1632C73F93A58C7802BA4D3171E4FFF3E714BE9B232212E73F4A239E1600F51F";
    public const string Source = "reviewed-published-npc-dialogue-v13";
    public const string InstanceCallerDescription =
        "Enter Medusa Island, Atlantis Portal, or Wonderland. Atlantis " +
        "welcomes Level 90-140 parties of exactly 3; Wonderland welcomes " +
        "Level 120+ parties of up to 5 on Saturday and Sunday before " +
        "11:00pm. Each instance allows at most 3 entries per player every " +
        "realm day. The first Atlantis entry is free. For its second and " +
        "third entries, each returning party member personally presents " +
        "one Opal; the leader starts after everyone who owes one is ready. " +
        "Wonderland does not require Opals.";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV12.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV12.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV12.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts)
    {
        var inherited = NpcDialogueBaselineV12.ApplyTextOverrides(texts);
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
                "The V13 Instance Caller text overlay requires both " +
                "capital callers exactly once.");
        }

        return result;
    }
}
