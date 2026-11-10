using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Corrects the Atlantis wording: all players receive three free daily
/// entries, and Opals buy additional entries after those are exhausted.
/// The complete V13 dialogue geometry remains immutable.
/// </summary>
internal static class NpcDialogueBaselineV14
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV13.ExpectedTextCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV13.ExpectedProfileCount;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV13.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV13.ExpectedMenuEntryCount;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV13.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "D4558D4DF02D82E0727896E08ECEFD634032B85A39D6151B0EC79B456257C902";
    public const string Source = "reviewed-published-npc-dialogue-v14";
    public const string InstanceCallerDescription =
        "Enter Medusa Island, Atlantis Portal, or Wonderland. Atlantis " +
        "welcomes Level 90-140 parties of exactly 3; Wonderland welcomes " +
        "Level 120+ parties of up to 5 on Saturday and Sunday before " +
        "11:00pm. Atlantis grants each player 3 free entries every realm " +
        "day. After a player uses all 3 free entries, one additional " +
        "Atlantis entry costs that player one Opal. Every party member who has " +
        "exhausted their own free entries must personally present one " +
        "Opal; the leader starts after everyone who owes one is ready. " +
        "Wonderland grants 3 free entries and does not require Opals.";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV13.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV13.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV13.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts)
    {
        var inherited = NpcDialogueBaselineV13.ApplyTextOverrides(texts);
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
                "The V14 Instance Caller text overlay requires both " +
                "capital callers exactly once.");
        }

        return result;
    }
}
