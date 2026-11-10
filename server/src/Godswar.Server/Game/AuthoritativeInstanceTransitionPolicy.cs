using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static class AuthoritativeInstanceTransitionPolicy
{
    public static bool IsSupported(
        byte sourceMapId,
        InstanceKind sourceKind,
        byte targetMapId,
        InstanceKind targetKind,
        byte characterCamp)
    {
        var sourceIsDungeon =
            DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(sourceMapId);
        var targetIsDungeon =
            DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(targetMapId);

        if (!sourceIsDungeon &&
            targetIsDungeon)
        {
            return sourceKind == InstanceKind.OpenWorld &&
                targetKind == InstanceKind.Dungeon;
        }

        if (!sourceIsDungeon ||
            targetIsDungeon ||
            sourceKind != InstanceKind.Dungeon ||
            targetKind != InstanceKind.OpenWorld)
        {
            return false;
        }

        var capitalMapId = characterCamp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        return targetMapId == capitalMapId;
    }
}
