using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Pins the dialogue release to the corrected V3 Arena placements and makes
/// each transporter's direction explicit. The complete V15 dialogue geometry
/// remains immutable.
/// </summary>
internal static class NpcDialogueBaselineV16
{
    public const int ExpectedTextCount =
        NpcDialogueBaselineV15.ExpectedTextCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV15.ExpectedProfileCount;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV15.ExpectedRouteCount;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV15.ExpectedMenuEntryCount;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV3.ExpectedRevision;
    public const string ExpectedRevision =
        "1E1885CCF91D3ABA78C8E275640F219FD46AE4FE19B4AA0A357690854A31F310";
    public const string Source = "reviewed-published-npc-dialogue-v16";
    public const string GatekeeperDescription =
        "Travel from the upper lobby to the lower Duel Arena.";
    public const string DoorkeeperDescription =
        "Return from the lower Duel Arena to the upper lobby.";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV15.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV15.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV15.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts)
    {
        var inherited = NpcDialogueBaselineV15.ApplyTextOverrides(texts);
        var gatekeeperCount = 0;
        var doorkeeperCount = 0;
        var result = inherited.Select(text =>
        {
            if (text.NpcKey ==
                DuelArenaTransporterProtocol.GatekeeperNpcKey)
            {
                gatekeeperCount++;
                return text with { Description = GatekeeperDescription };
            }

            if (text.NpcKey ==
                DuelArenaTransporterProtocol.DoorkeeperNpcKey)
            {
                doorkeeperCount++;
                return text with { Description = DoorkeeperDescription };
            }

            return text;
        }).ToArray();
        if (gatekeeperCount != 1 || doorkeeperCount != 1)
        {
            throw new InvalidDataException(
                "The V16 Arena text overlay requires both transporters " +
                "exactly once.");
        }

        return result;
    }
}
