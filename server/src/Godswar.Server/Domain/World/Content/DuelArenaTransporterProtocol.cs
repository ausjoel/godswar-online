namespace Godswar.Server.Domain.World.Content;

internal enum DuelArenaSection : byte
{
    UpperLobby = 1,
    LowerArena = 2
}

internal sealed record DuelArenaTransportDestination(
    DuelArenaSection Section,
    float TargetX,
    float TargetZ);

/// <summary>
/// Finite same-scene travel surface for Arena's two disconnected collision
/// components. Coordinates are server-owned points verified against the
/// stock Arena.hmp walkability grid.
/// </summary>
internal static class DuelArenaTransporterProtocol
{
    public const byte MapId = 57;
    public const string GatekeeperNpcKey = "Arena_003";
    public const string DoorkeeperNpcKey = "Arena_002";
    internal const uint LegacyGatekeeperNpcId = 57_003;
    internal const uint LegacyDoorkeeperNpcId = 57_002;
    public const uint GatekeeperNpcId = 5_703;
    public const uint DoorkeeperNpcId = 5_702;
    public const int DialogIndex = 1;
    public const int InitialRequestSubId = -1;
    public const int TravelSubId = 1001;
    public const int ActionPacketBytes = 92;
    public const int FunctionArgumentCount = 18;
    public const float MaximumInteractionDistance = 12f;

    public const float GatekeeperSpawnX = -92f;
    public const float GatekeeperSpawnZ = 92f;
    public const float DoorkeeperSpawnX = -7f;
    public const float DoorkeeperSpawnZ = 28f;
    public const float UpperArrivalX = -82f;
    public const float UpperArrivalZ = 92f;
    public const float LowerArrivalX = -6f;
    public const float LowerArrivalZ = 20f;

    public static readonly TimeSpan MenuContextLifetime =
        TimeSpan.FromMinutes(2);

    public static IReadOnlyList<int> InitialMenuSubIds { get; } =
        Array.AsReadOnly(new[] { TravelSubId });

    public static bool IsEndpoint(string npcKey, uint interactionId) =>
        (npcKey, interactionId) is
            (GatekeeperNpcKey, GatekeeperNpcId) or
            (DoorkeeperNpcKey, DoorkeeperNpcId);

    public static bool TryResolveDestination(
        string npcKey,
        uint interactionId,
        byte sourceMapId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out DuelArenaTransportDestination destination)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        destination = null!;
        if (sourceMapId != MapId ||
            dialogIndex != DialogIndex ||
            subId != TravelSubId ||
            !HasExactPath(arguments))
        {
            return false;
        }

        destination = (npcKey, interactionId) switch
        {
            (GatekeeperNpcKey, GatekeeperNpcId) => new(
                DuelArenaSection.LowerArena,
                LowerArrivalX,
                LowerArrivalZ),
            (DoorkeeperNpcKey, DoorkeeperNpcId) => new(
                DuelArenaSection.UpperLobby,
                UpperArrivalX,
                UpperArrivalZ),
            _ => null!
        };
        return destination is not null;
    }

    private static bool HasExactPath(IReadOnlyList<int> arguments)
    {
        if (arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        return arguments.All(static argument => argument == -1);
    }
}
