using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    // B22 B23-R (B23-S question 6): the Aether Sanctum way-out NPC is a GM placement of Mycenae's
    // teleporter (npc key Mycenae_All_013), and TransporterProtocol pins that key to interaction id
    // 59721, so it must keep that id. Quest tables are keyed on the interaction id too, so on map 70 it
    // showed the north quest giver's quest mark and opened its quest page. On map 70 the id has no
    // quest function: no 10077/10080 tables, no quest page. Mycenae (map 6) is unchanged.
    internal static bool IsSanctumExitTransporter(int mapId, uint interactionId) =>
        mapId == AetherSanctumPolicy.MapId && interactionId == TransporterProtocol.MycenaeNpcId;

    private bool SuppressQuestFunctionHere(uint interactionId) =>
        _character is not null && IsSanctumExitTransporter(_character.CurrentMap, interactionId);
}
