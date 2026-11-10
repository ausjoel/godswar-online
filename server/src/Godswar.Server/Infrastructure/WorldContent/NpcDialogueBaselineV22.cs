using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Describes Atlantis solo/party admission and the reviewed timed score goal.
/// All V21 routes, other texts, and spawn dependencies remain unchanged.
/// </summary>
internal static class NpcDialogueBaselineV22
{
    public const int ExpectedTextCount = NpcDialogueBaselineV21.ExpectedTextCount;
    public const int ExpectedProfileCount = NpcDialogueBaselineV21.ExpectedProfileCount;
    public const int ExpectedRouteCount = NpcDialogueBaselineV21.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount = NpcDialogueBaselineV21.ExpectedMenuEntryCount;
    public const int ExpectedHashedEntryCount = ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision = NpcDialogueBaselineV21.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "BCAEB3D7F36801B903B3883D7F4F542F5D16A8464AD3AD16B50BBD17854275A9";
    public const string Source = "reviewed-published-npc-dialogue-v22";
    public const string InstanceCallerDescription =
        "Enter Medusa Island, Atlantis Portal, or Wonderland. Atlantis " +
        "welcomes Level 90-140 players solo or in parties of 1-5; Wonderland welcomes " +
        "Level 120+ parties of up to 5 on Saturday and Sunday before " +
        "11:00pm. Atlantis grants each player 3 free entries every realm " +
        "day. After a player uses all 3 free entries, one additional " +
        "Atlantis entry costs that player one Opal. Every party member who has " +
        "exhausted their own free entries must personally present one " +
        "Opal; the leader starts after everyone who owes one is ready. " +
        "Wonderland grants 3 free entries and does not require Opals. " +
        "In Atlantis, normal monsters give 1 point, elite monsters 10, and bosses 50. " +
        "Reach 850 points within 40 minutes to complete Atlantis.";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV21.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV21.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV21.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts)
    {
        var inherited = NpcDialogueBaselineV21.ApplyTextOverrides(texts);
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
                "The V22 Instance Caller text overlay requires both " +
                "capital callers exactly once.");
        }

        return result;
    }
}
