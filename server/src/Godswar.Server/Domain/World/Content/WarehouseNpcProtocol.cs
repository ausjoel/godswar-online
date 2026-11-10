namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Finite stock endpoints for the character-owned normal warehouse. Source
/// catalog actor IDs are deliberately not accepted here; interaction IDs are
/// the identities published into the authoritative map snapshot.
/// </summary>
internal static class WarehouseNpcProtocol
{
    public const uint AthensWarehouseNpcId = 5165;
    public const uint SpartaWarehouseNpcId = 47750;
    public const uint DuelArenaWarehouseNpcId = 5202;
    // Secondary capital warehouse tellers. The stock client ships two normal
    // storage actors per capital; both advertise the same warehouse dialog, so
    // both are authoritative endpoints over the one character warehouse.
    public const uint AthensSecondaryWarehouseNpcId = 5238;
    public const uint SpartaSecondaryWarehouseNpcId = 5097;
    public const uint AthensManagerNpcId = 5272;
    public const uint SpartaManagerNpcId = 5131;

    public const int ManagerDialogIndex = 106;
    public const int ManagerInitialRequestSubId = -1;
    public const int ManagerActionSubId = 100;
    public const int ManagerGenericResultSubId = 999;

    public static IReadOnlyList<int> ManagerInitialMenuSubIds { get; } =
        [ManagerActionSubId];

    public static bool IsWarehouseEndpoint(
        string npcKey,
        uint interactionId) =>
        (npcKey, interactionId) is
            ("Athens_025", AthensWarehouseNpcId) or
            ("Sparta_023", SpartaWarehouseNpcId) or
            ("Athens_100", AthensSecondaryWarehouseNpcId) or
            ("Sparta_100", SpartaSecondaryWarehouseNpcId) or
            ("DuelArena_001", DuelArenaWarehouseNpcId);

    public static bool IsDuelArenaWarehouseEndpoint(
        string npcKey,
        uint interactionId) =>
        npcKey == "DuelArena_001" &&
        interactionId == DuelArenaWarehouseNpcId;

    public static string ClientScriptKey(string npcKey, uint interactionId) =>
        IsDuelArenaWarehouseEndpoint(npcKey, interactionId)
            ? "Sparta_023"
            : npcKey;

    public static bool IsManagerEndpoint(
        string npcKey,
        uint interactionId) =>
        (npcKey, interactionId) is
            ("Athens_134", AthensManagerNpcId) or
            ("Sparta_134", SpartaManagerNpcId);
}
