using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Pins the Arena dialogue surface to the clustered V5 spawn geometry. The
/// strict dialogue loader requires an exact spawn-revision match, so this
/// immutable release makes one wording-only clarification to obtain a unique
/// dialogue revision while retaining the complete V17 routes and labels.
/// </summary>
internal static class NpcDialogueBaselineV18
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV17.ExpectedTextCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV17.ExpectedProfileCount;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV17.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV17.ExpectedMenuEntryCount;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV5.ExpectedRevision;
    public const string ExpectedRevision =
        "569F836E7110BF01610E9A6AF0A2E2B6806ECE4274BFE62D8AB0A18F7B7EE454";
    public const string Source = "reviewed-published-npc-dialogue-v18";
    public const string GatekeeperDescription =
        "Travel from this upper lobby to the lower Duel Arena.";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV17.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV17.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV17.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts)
    {
        var inherited = NpcDialogueBaselineV17.ApplyTextOverrides(texts);
        var gatekeeperCount = 0;
        var result = inherited.Select(text =>
        {
            if (text.NpcKey !=
                DuelArenaTransporterProtocol.GatekeeperNpcKey)
            {
                return text;
            }

            gatekeeperCount++;
            return text with { Description = GatekeeperDescription };
        }).ToArray();
        if (gatekeeperCount != 1)
        {
            throw new InvalidDataException(
                "The V18 Arena text overlay requires the Gatekeeper " +
                "exactly once.");
        }

        return result;
    }
}
