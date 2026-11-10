namespace Godswar.Server.Domain.World.Instances;

/// <summary>
/// Identifies legacy content maps that may only be hosted by an exact,
/// authoritative dungeon instance. These maps must never fall back to a
/// map-only/default open-world runtime.
/// </summary>
internal static class DynamicDungeonContentMapPolicy
{
    public const byte MedusaIslandMapId = 200;
    public const byte MedusaIslandAlternateMapId = 204;
    public const byte AtlantisPortalMapId = 205;
    public const byte WonderlandMapId = 207;
    public const byte HarborAttackFirstMapId = 208;
    public const byte HarborAttackSecondMapId = 209;
    // TRAINING: the Training Ground (Trials of Heracles) runs as exact instances too.
    public const byte TrainingGroundMapId = 210;
    // WARRIOR (F-51): the four Warriors' Training Grounds (Field_Test 1-4) run as exact instances.
    public const byte WarriorTrainingGround1MapId = 201;
    public const byte WarriorTrainingGround2MapId = 202;
    public const byte WarriorTrainingGround3MapId = 203;
    public const byte WarriorTrainingGround4MapId = 206;

    public static bool IsDynamicDungeonMap(int mapId) =>
        mapId is
            MedusaIslandMapId or
            MedusaIslandAlternateMapId or
            AtlantisPortalMapId or
            WonderlandMapId or
            HarborAttackFirstMapId or
            HarborAttackSecondMapId or
            TrainingGroundMapId or
            WarriorTrainingGround1MapId or
            WarriorTrainingGround2MapId or
            WarriorTrainingGround3MapId or
            WarriorTrainingGround4MapId;

    public static bool IsWarriorTrainingGroundMap(int mapId) =>
        mapId is WarriorTrainingGround1MapId or WarriorTrainingGround2MapId or
            WarriorTrainingGround3MapId or WarriorTrainingGround4MapId;

    public static bool IsHarborAttackMap(int mapId) =>
        mapId is HarborAttackFirstMapId or HarborAttackSecondMapId;

    public static bool IsMedusaMap(int mapId) =>
        mapId is MedusaIslandMapId or MedusaIslandAlternateMapId;
}
