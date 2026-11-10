using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Pins the Arena dialogue surface to the corrected V6 upper-lobby geometry.
/// The strict dialogue loader requires an exact spawn-revision match, so this
/// immutable release makes one wording-only change while retaining V18's
/// complete routes and labels.
/// </summary>
internal static class NpcDialogueBaselineV19
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV18.ExpectedTextCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV18.ExpectedProfileCount;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV18.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV18.ExpectedMenuEntryCount;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV6.ExpectedRevision;
    public const string ExpectedRevision =
        "6E7D61ED433F02A50BD6F2C86280F9B504C333C49EE1B2EE36DD6618EE90BF54";
    public const string Source = "reviewed-published-npc-dialogue-v19";
    public const string GatekeeperDescription =
        "Travel from this upper lobby into the lower Duel Arena.";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV18.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV18.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV18.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts)
    {
        var inherited = NpcDialogueBaselineV18.ApplyTextOverrides(texts);
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
                "The V19 Arena text overlay requires the Gatekeeper " +
                "exactly once.");
        }

        return result;
    }
}
