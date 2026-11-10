using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// 复活流程专用:把指定地图上所有以该角色为目标的怪物解除仇恨。
    /// 失去最后一名目标的怪物会走回自己的刷新点(HomeX/HomeZ),
    /// 而不是留在原地继续纠缠刚复活的挑战者。
    /// </summary>
    internal void ClearMonsterAggroForCharacter(
        byte legacyMapId,
        ClientSession routingSession,
        int characterId,
        DateTimeOffset now)
    {
        if (!TryResolveWorldInstance(
                legacyMapId,
                routingSession,
                out var runtime))
        {
            return;
        }

        InvokeWorldOwner(
            runtime,
            map => map.ClearMonsterAggroForCharacter(
                characterId,
                now));
    }
}
