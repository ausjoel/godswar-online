namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Client-native support actors placed in Arena's upper lobby. Their stock
/// identities and appearances come from the client catalog; the server owns
/// the terrain-validated placements.
/// </summary>
internal static class DuelArenaNpcRoster
{
    public const string VendorNpcKey = "Arena_001";
    public const string VendorTemplateKey = "Arena_001_FemMale14";
    // V2-V5 used five-digit authored IDs. Keep those identities explicit so
    // the sealed historical releases remain reproducible while the current
    // release uses the same native NPC object-ID range as the stock client.
    internal const uint LegacyVendorNpcId = 57_001;
    public const uint VendorNpcId = 5_701;
    public const float VendorSpawnX = -84f;
    public const float VendorSpawnZ = 108f;
    public const float VendorFacing = 3.017238f;

    public const string WardNpcKey = "Arena_004";
    public const string WardTemplateKey = "Arena_004_Male18";
    internal const uint LegacyWardNpcId = 57_004;
    public const uint WardNpcId = 5_704;
    public const float WardSpawnX = -112f;
    public const float WardSpawnZ = 84f;
    public const float WardFacing = 1.310194f;

    public const string PhysicianNpcKey = "Arena_005";
    public const string PhysicianTemplateKey = "Arena_005_yishi";
    internal const uint LegacyPhysicianNpcId = 57_005;
    public const uint PhysicianNpcId = 5_705;
    public const float PhysicianSpawnX = -84f;
    public const float PhysicianSpawnZ = 72f;
    public const float PhysicianFacing = 0.099669f;
}
