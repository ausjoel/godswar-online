namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Native Arena travel observed on 2026-09-07. The Gatekeeper admits players
/// and the Ward returns them; both act on the initial function request.
/// </summary>
internal static class DuelArenaCapturedTransportProtocol
{
    public const int GatekeeperDialogIndex = 87;
    public const int WardDialogIndex = 88;
    public const int TravelSubId = -1;
    public const int MeaningfulArgumentCount = 6;
    public const string GatekeeperClientScriptKey = "Arena_002";

    public static bool IsCapturedEndpoint(string npcKey, uint interactionId) =>
        (npcKey, interactionId) is
            (DuelArenaCapturedLayout.GatekeeperNpcKey,
                DuelArenaCapturedLayout.GatekeeperNpcId) or
            (DuelArenaCapturedLayout.WardNpcKey,
                DuelArenaCapturedLayout.WardNpcId);

    public static bool IsAllowedClientScriptKey(string npcKey, string scriptKey) =>
        string.Equals(npcKey, scriptKey, StringComparison.Ordinal) ||
        (npcKey == DuelArenaCapturedLayout.GatekeeperNpcKey &&
            scriptKey == GatekeeperClientScriptKey);

    public static bool IsCapturedRoute(NpcDialogueRouteDefinition route) =>
        route.Behavior == NpcDialogueBehavior.DuelArenaTransporter &&
        route.RouteOrder == 0 &&
        route.InitialMenuSubIds.Length == 1 &&
        route.InitialMenuSubIds[0] == TravelSubId &&
        (route.NpcKey, route.ClientScriptKey, route.DialogIndex) is
            (DuelArenaCapturedLayout.GatekeeperNpcKey,
                GatekeeperClientScriptKey, GatekeeperDialogIndex) or
            (DuelArenaCapturedLayout.WardNpcKey,
                DuelArenaCapturedLayout.WardNpcKey, WardDialogIndex);

    public static bool TryResolveDestination(
        string npcKey,
        uint interactionId,
        byte sourceMapId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out DuelArenaTransportDestination destination,
        int entryIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        destination = null!;
        if (sourceMapId != DuelArenaCapturedLayout.MapId ||
            subId != TravelSubId ||
            arguments.Count != DuelArenaTransporterProtocol.FunctionArgumentCount ||
            !arguments.Take(MeaningfulArgumentCount).All(static value => value == -1))
        {
            return false;
        }

        // The native 92-byte request initializes six function arguments.
        // Its remaining twelve words are padding/stack residue, never input
        // to an operation, destination, account, or world-instance decision.
        if ((npcKey, interactionId, dialogIndex) is
            (DuelArenaCapturedLayout.GatekeeperNpcKey,
                DuelArenaCapturedLayout.GatekeeperNpcId, GatekeeperDialogIndex))
        {
            return DuelArenaEntryDestinations.TryResolve(entryIndex, out destination);
        }

        destination = (npcKey, interactionId, dialogIndex) switch
        {
            (DuelArenaCapturedLayout.WardNpcKey,
                DuelArenaCapturedLayout.WardNpcId,
                WardDialogIndex) => new(
                    DuelArenaSection.UpperLobby,
                    DuelArenaCapturedLayout.UpperArrivalX,
                    DuelArenaCapturedLayout.UpperArrivalZ),
            _ => null!
        };
        return destination is not null;
    }
}
