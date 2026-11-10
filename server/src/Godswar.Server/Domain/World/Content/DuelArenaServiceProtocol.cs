using System.Collections.Immutable;

namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Native Arena support dialogues observed at 16:33 on September 7.
/// Includes the subsequently captured Doorkeeper exit. Healing payments and
/// rewards are not inferred from the browsing responses.
/// </summary>
internal static class DuelArenaServiceProtocol
{
    public const int PhysicianDialogIndex = 32;
    public const int DoorkeeperDialogIndex = 88;
    public const int AirDropDialogIndex = 95;
    public const int InitialRequestSubId = -1;
    public static ImmutableArray<int> PhysicianInitialMenu { get; } = [1, 100, 101, 102];
    public static ImmutableArray<int> SilentInitialAction { get; } = [-1];

    public static bool IsCapturedRoute(NpcDialogueRouteDefinition route)
    {
        if (route.Behavior != NpcDialogueBehavior.DuelArenaServices ||
            route.RouteOrder != 0 || route.NpcKey != route.ClientScriptKey ||
            route.InitialMenuSubIds.IsDefault)
        {
            return false;
        }

        return (route.NpcKey, route.DialogIndex) switch
        {
            (DuelArenaCapturedLayout.PhysicianNpcKey, PhysicianDialogIndex) =>
                route.InitialMenuSubIds.SequenceEqual(PhysicianInitialMenu),
            (DuelArenaCapturedLayout.DoorkeeperNpcKey, DoorkeeperDialogIndex) or
            (DuelArenaCapturedLayout.AirDropNpcKey, AirDropDialogIndex) =>
                route.InitialMenuSubIds.SequenceEqual(SilentInitialAction),
            _ => false
        };
    }

    public static bool IsAllowed(NpcSpawnDefinition npc, NpcDialogueRouteDefinition route) =>
        npc.MapId == DuelArenaCapturedLayout.MapId &&
        npc.NpcKey == route.NpcKey && IsCapturedRoute(route) &&
        (npc.NpcKey, npc.InteractionId) is
            (DuelArenaCapturedLayout.PhysicianNpcKey, DuelArenaCapturedLayout.PhysicianNpcId) or
            (DuelArenaCapturedLayout.DoorkeeperNpcKey, DuelArenaCapturedLayout.DoorkeeperNpcId) or
            (DuelArenaCapturedLayout.AirDropNpcKey, DuelArenaCapturedLayout.AirDropNpcId);

    public static bool IsInitialRequest(int subId, IReadOnlyList<int> arguments) =>
        subId == InitialRequestSubId &&
        arguments.Count == DuelArenaTransporterProtocol.FunctionArgumentCount &&
        arguments.Take(DuelArenaCapturedTransportProtocol.MeaningfulArgumentCount)
            .All(static argument => argument == -1);
}
