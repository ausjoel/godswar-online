namespace Godswar.Server.Domain.World.Content;

internal sealed record DuelArenaCapturedNpc(
    string NpcKey,
    string TemplateKey,
    uint ObjectId,
    float X,
    float Z);

/// <summary>
/// Map-57 NPCs observed in external-20260907-143201-403.log at
/// 15:40:25, 15:40:56, and 15:41:02 +12:00. Template keys use the local
/// client namespace; the capture's gwprivate_ prefix is not an actor ID.
/// </summary>
internal static class DuelArenaCapturedLayout
{
    public const byte MapId = 57;
    public const uint AppearanceType = 0x0211;
    public const float Facing = 2.3218750953674316f;

    public const float UpperArrivalX = -104f;
    public const float UpperArrivalZ = 96f;
    public const float LowerArrivalX = -5f;
    public const float LowerArrivalZ = 32f;

    public const string VendorNpcKey = "Arena_001";
    public const string VendorTemplateKey = "Arena_001_FemMale14";
    public const uint VendorNpcId = 5_198;
    public const float VendorSpawnX = -108f;
    public const float VendorSpawnZ = 81f;

    public const string DoorkeeperNpcKey = "Arena_002";
    public const string DoorkeeperTemplateKey = "Arena_002_Male18";
    public const uint DoorkeeperNpcId = 5_197;
    public const float DoorkeeperSpawnX = -110f;
    public const float DoorkeeperSpawnZ = 102f;

    public const string GatekeeperNpcKey = "Arena_003";
    public const string GatekeeperTemplateKey = "Arena_003_Male18";
    public const uint GatekeeperNpcId = 5_199;
    public const float GatekeeperSpawnX = -99f;
    public const float GatekeeperSpawnZ = 68f;

    public const string WardNpcKey = "Arena_004";
    public const string WardTemplateKey = "Arena_004_Male18";
    public const uint WardNpcId = 5_201;
    public const float WardSpawnX = -48f;
    public const float WardSpawnZ = 40f;

    public const string PhysicianNpcKey = "Arena_005";
    public const string PhysicianTemplateKey = "Arena_005_yishi";
    public const uint PhysicianNpcId = 5_200;
    public const float PhysicianSpawnX = -93f;
    public const float PhysicianSpawnZ = 100f;

    public const string AirDropNpcKey = "Arena_006";
    public const string AirDropTemplateKey = "Arena_006_AirDrop";
    public const uint AirDropNpcId = 5_203;
    public const float AirDropSpawnX = -118.98906707763672f;
    public const float AirDropSpawnZ = 98.9961166381836f;

    public const string DuelArenaNpcKey = "DuelArena_001";
    public const string DuelArenaTemplateKey = "DuelArena_001_Male3";
    public const uint DuelArenaNpcId = 5_202;
    public const float DuelArenaSpawnX = -103.50323486328125f;
    public const float DuelArenaSpawnZ = 103.38972473144531f;

    public static IReadOnlyList<DuelArenaCapturedNpc> Npcs { get; } =
        Array.AsReadOnly<DuelArenaCapturedNpc>(
        [
            new(VendorNpcKey, VendorTemplateKey, VendorNpcId,
                VendorSpawnX, VendorSpawnZ),
            new(DoorkeeperNpcKey, DoorkeeperTemplateKey, DoorkeeperNpcId,
                DoorkeeperSpawnX, DoorkeeperSpawnZ),
            new(GatekeeperNpcKey, GatekeeperTemplateKey, GatekeeperNpcId,
                GatekeeperSpawnX, GatekeeperSpawnZ),
            new(WardNpcKey, WardTemplateKey, WardNpcId,
                WardSpawnX, WardSpawnZ),
            new(PhysicianNpcKey, PhysicianTemplateKey, PhysicianNpcId,
                PhysicianSpawnX, PhysicianSpawnZ),
            new(AirDropNpcKey, AirDropTemplateKey, AirDropNpcId,
                AirDropSpawnX, AirDropSpawnZ),
            new(DuelArenaNpcKey, DuelArenaTemplateKey, DuelArenaNpcId,
                DuelArenaSpawnX, DuelArenaSpawnZ)
        ]);
}
