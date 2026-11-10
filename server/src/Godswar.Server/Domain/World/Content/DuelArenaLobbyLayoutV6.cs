namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Corrects the upper-lobby cluster for the persisted AresDev checkpoint.
/// V5's Vendor was one AOI row beyond that checkpoint; the other four Arena
/// placements remain unchanged. Terrain evidence is pinned from the stock
/// Arena.hmp consumed by the client.
/// </summary>
internal static class DuelArenaLobbyLayoutV6
{
    public const float ObservedCheckpointX = -87.53f;
    public const float ObservedCheckpointZ = 63.98f;

    public const float VendorSpawnX = -74f;
    public const float VendorSpawnZ = 94f;

    public const float MinimumUpperActorSeparation = 10f;
    public const float MaximumUpperActorSeparation = 30f;
    public const float MaximumDistanceFromUpperArrival = 20f;
    public const float MinimumTerrainClearance = 14f;

    public const string ArenaHmpSha256 =
        "08ED1AD3CCB9E806175883F87791B6D953F07AB294A10BE0FD06536538537F24";
    public const string ArenaHmpBlockSha256 =
        "86B8EABBF9A87B218715C9140BD2199D4CF34664D5AAC25C7941B7D2F6D87CED";
    public const int ArenaHmpBlockByteOffset = 47_104;
    public const int ArenaHmpBlockCellsPerAxis = 2_048;
    public const byte UnblockedValue = 0;

    public static IReadOnlyList<DuelArenaTerrainPointEvidence>
        UpperActorTerrainEvidence { get; } =
    [
        new(
            DuelArenaNpcRoster.VendorNpcKey,
            VendorSpawnX,
            VendorSpawnZ,
            728,
            648,
            UnblockedValue,
            22f),
        new(
            DuelArenaTransporterProtocol.GatekeeperNpcKey,
            DuelArenaTransporterProtocol.GatekeeperSpawnX,
            DuelArenaTransporterProtocol.GatekeeperSpawnZ,
            656,
            656,
            UnblockedValue,
            20.872f),
        new(
            DuelArenaNpcRoster.WardNpcKey,
            DuelArenaLobbyLayoutV5.WardSpawnX,
            DuelArenaLobbyLayoutV5.WardSpawnZ,
            616,
            656,
            UnblockedValue,
            14.235f),
        new(
            DuelArenaNpcRoster.PhysicianNpcKey,
            DuelArenaLobbyLayoutV5.PhysicianSpawnX,
            DuelArenaLobbyLayoutV5.PhysicianSpawnZ,
            672,
            696,
            UnblockedValue,
            26f)
    ];

    public static bool TryProjectToHmpBlock(
        float x,
        float z,
        out DuelArenaHmpBlockCell cell)
    {
        cell = default;
        if (!float.IsFinite(x) ||
            !float.IsFinite(z) ||
            x is < -256f or >= 256f ||
            z is <= -256f or > 256f)
        {
            return false;
        }

        cell = new DuelArenaHmpBlockCell(
            (int)MathF.Floor((x + 256f) * 4f),
            (int)MathF.Floor((256f - z) * 4f));
        return cell.X is >= 0 and < ArenaHmpBlockCellsPerAxis &&
               cell.Z is >= 0 and < ArenaHmpBlockCellsPerAxis;
    }
}

internal readonly record struct DuelArenaHmpBlockCell(int X, int Z);

internal sealed record DuelArenaTerrainPointEvidence(
    string NpcKey,
    float X,
    float Z,
    int BlockX,
    int BlockZ,
    byte DecodedBlockValue,
    float BlockedCellClearance);
