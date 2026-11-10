namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Compact upper-lobby placement for Arena's three support actors. The
/// points frame the existing Gatekeeper and arrival without blocking their
/// direct approach, and are verified against the stock Arena.hmp block table.
/// </summary>
internal static class DuelArenaLobbyLayoutV5
{
    public const float VendorSpawnX = -88f;
    public const float VendorSpawnZ = 102f;
    public const float VendorFacing = 2.601173f;

    public const float WardSpawnX = -102f;
    public const float WardSpawnZ = 92f;
    public const float WardFacing = 1.570796f;

    public const float PhysicianSpawnX = -88f;
    public const float PhysicianSpawnZ = 82f;
    public const float PhysicianFacing = 0.540420f;

    public const float MaximumDistanceFromUpperArrival = 20f;
    public const float MinimumSupportActorSeparation = 17f;
}
