using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const string FarmReturnUnavailableMessage =
        "The return teleport is temporarily unavailable.";

    /// <summary>
    /// Sends a fighter on the farm back to their own capital, which is what the
    /// farm's Returning Helper exists for.
    /// </summary>
    /// <remarks>
    /// The destination is the character's own faction capital, the same pair
    /// <c>GameDefaults</c> uses for every other homeward egress, and the
    /// arrival point is that capital's own published landing rather than
    /// anything the client sent.
    /// </remarks>
    private async Task HandleLelantineFarmReturnAsync(
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            dialogIndex != LelantineFarmProtocol.TeleportDialogIndex ||
            subId != LelantineFarmProtocol.FarmReturnSubId ||
            _character.CurrentMap != LelantineFarmProtocol.MapId ||
            !LelantineFarmProtocol.IsReturningHelper(route.NpcKey) ||
            !LelantineFarmProtocol.TryResolve(
                route.NpcKey,
                npcId,
                out _))
        {
            Console.Error.WriteLine(
                "[farm] rejected malformed return action " +
                $"character={_character?.Name ?? "<none>"} npc={npcId} " +
                $"dialog={dialogIndex} sub={subId}");
            return;
        }

        var targetMapId = _character.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        var outcome = await TryBeginMapTransitionAsync(
            targetMapId,
            GameDefaults.StartingPositionX,
            GameDefaults.StartingPositionZ,
            $"npc-farm-return:{route.NpcKey}:{subId}",
            cancellationToken);
        if (outcome == SceneTransitionOutcome.CommittedRequiresReconnect)
        {
            return;
        }

        if (outcome != SceneTransitionOutcome.CommittedAwaitingReadiness)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(FarmReturnUnavailableMessage),
                cancellationToken,
                "FarmReturnUnavailable");
            Console.Error.WriteLine(
                "[farm] return rejected character=" + _character.Name +
                $" npc={npcId} target={targetMapId}");
            return;
        }

        Console.WriteLine(
            "[farm] returned character=" + _character.Name +
            $" npc={npcId} map={LelantineFarmProtocol.MapId}->{targetMapId}");
    }
}
