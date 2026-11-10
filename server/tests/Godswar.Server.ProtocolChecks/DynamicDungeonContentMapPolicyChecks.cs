using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class DynamicDungeonContentMapPolicyChecks
{
    public const string CheckName =
        "Dynamic-dungeon exact-instance map boundary";

    public static Task RunAsync()
    {
        byte[] dungeonMaps = [200, 201, 202, 203, 204, 205, 206, 207, 208, 209, 210]; // TRAINING: 210; WARRIOR (F-51): 201-203/206
        foreach (var mapId in dungeonMaps)
        {
            Check.True(
                DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(mapId),
                $"map {mapId} requires an exact dynamic-dungeon identity");
            Check.True(
                AuthoritativeInstanceTransitionPolicy.IsSupported(
                    GameDefaults.AthensCapitalMap,
                    InstanceKind.OpenWorld,
                    mapId,
                    InstanceKind.Dungeon,
                    GameDefaults.AthensCamp),
                $"authoritative ingress accepts dynamic dungeon {mapId}");
            Check.True(
                AuthoritativeInstanceTransitionPolicy.IsSupported(
                    mapId,
                    InstanceKind.Dungeon,
                    GameDefaults.SpartaCapitalMap,
                    InstanceKind.OpenWorld,
                    GameDefaults.SpartaCamp),
                $"authoritative egress accepts dynamic dungeon {mapId}");
        }

        foreach (var mapId in new[] { -1, 0, 1, 199, 211 }) // WARRIOR (F-51): 201/203/206 are now dungeons
        {
            Check.True(
                !DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(mapId),
                $"ordinary/special map {mapId} is outside the dungeon set");
        }

        Check.True(
            DynamicDungeonContentMapPolicy.IsMedusaMap(200) &&
            DynamicDungeonContentMapPolicy.IsMedusaMap(204) &&
            !DynamicDungeonContentMapPolicy.IsMedusaMap(205) &&
            !DynamicDungeonContentMapPolicy.IsMedusaMap(207),
            "Medusa-specific behavior remains scoped to maps 200 and 204");
        Check.True(
            DynamicDungeonContentMapPolicy.IsHarborAttackMap(208) &&
            DynamicDungeonContentMapPolicy.IsHarborAttackMap(209) &&
            !DynamicDungeonContentMapPolicy.IsHarborAttackMap(207) &&
            !DynamicDungeonContentMapPolicy.IsHarborAttackMap(205),
            "港湾遇袭 behavior is scoped to the two Salame maps");
        Check.True(
            !AuthoritativeInstanceTransitionPolicy.IsSupported(
                GameDefaults.AthensCapitalMap,
                InstanceKind.OpenWorld,
                205,
                InstanceKind.OpenWorld,
                GameDefaults.AthensCamp) &&
            !AuthoritativeInstanceTransitionPolicy.IsSupported(
                205,
                InstanceKind.Dungeon,
                207,
                InstanceKind.Dungeon,
                GameDefaults.AthensCamp) &&
            !AuthoritativeInstanceTransitionPolicy.IsSupported(
                207,
                InstanceKind.Dungeon,
                GameDefaults.SpartaCapitalMap,
                InstanceKind.OpenWorld,
                GameDefaults.AthensCamp),
            "kind mismatches, dungeon hopping, and wrong-capital egress fail " +
            "closed");

        return Task.CompletedTask;
    }
}
