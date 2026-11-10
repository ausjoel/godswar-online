using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Adds the Physician menu and native Doorkeeper/Airdrop initial actions
/// captured after the V20 Arena travel publication. Texts and spawns remain
/// pinned to the same reviewed seven-actor release.
/// </summary>
internal static class NpcDialogueBaselineV21
{
    public const int ExpectedTextCount = NpcDialogueBaselineV20.ExpectedTextCount;
    public const int ExpectedProfileCount =
        NpcDialogueBaselineV20.ExpectedProfileCount + 3;
    public const int ExpectedRouteCount =
        NpcDialogueBaselineV20.ExpectedRouteCount + 3;
    public const int ExpectedMenuEntryCount =
        NpcDialogueBaselineV20.ExpectedMenuEntryCount + 6;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision = NpcContentBaselineV7.ExpectedRevision;
    public const string ExpectedRevision =
        "0286976617060763C4B6491739D1724C0104E6E078B4F61D9D8FE59E6E910610";
    public const string Source = "reviewed-published-npc-dialogue-v21";
    public const string PhysicianProfileKey = "duel_arena_physician";
    public const string DoorkeeperProfileKey = "duel_arena_doorkeeper";
    public const string AirDropProfileKey = "duel_arena_airdrop";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV20.Profiles,
        new(PhysicianProfileKey, DuelArenaServiceProtocol.PhysicianDialogIndex,
            NpcDialogueBehavior.DuelArenaServices,
            DuelArenaServiceProtocol.InitialRequestSubId,
            DuelArenaServiceProtocol.PhysicianInitialMenu),
        // [-1] records the initial native request. These two endpoints do
        // not publish a follow-up menu response in the capture.
        new(DoorkeeperProfileKey, DuelArenaServiceProtocol.DoorkeeperDialogIndex,
            NpcDialogueBehavior.DuelArenaServices,
            DuelArenaServiceProtocol.InitialRequestSubId,
            DuelArenaServiceProtocol.SilentInitialAction),
        new(AirDropProfileKey, DuelArenaServiceProtocol.AirDropDialogIndex,
            NpcDialogueBehavior.DuelArenaServices,
            DuelArenaServiceProtocol.InitialRequestSubId,
            DuelArenaServiceProtocol.SilentInitialAction)
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV20.Bindings,
        new("Arena_005", "Arena_005", PhysicianProfileKey),
        new("Arena_002", "Arena_002", DoorkeeperProfileKey),
        new("Arena_006", "Arena_006", AirDropProfileKey)
    ];

    public static NpcDialogueRouteDefinition[] CreateRoutes()
    {
        var profiles = Profiles.ToDictionary(
            static profile => profile.ProfileKey, StringComparer.Ordinal);
        return Bindings
            .OrderBy(static binding => binding.NpcKey, StringComparer.Ordinal)
            .ThenBy(static binding => binding.RouteOrder)
            .Select(binding =>
            {
                var profile = profiles[binding.ProfileKey];
                return new NpcDialogueRouteDefinition(
                    binding.NpcKey, binding.ClientScriptKey, profile.DialogIndex,
                    profile.Behavior, profile.InitialMenuSubIds)
                {
                    RouteOrder = binding.RouteOrder
                };
            })
            .ToArray();
    }

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts) =>
        NpcDialogueBaselineV20.ApplyTextOverrides(texts);
}
