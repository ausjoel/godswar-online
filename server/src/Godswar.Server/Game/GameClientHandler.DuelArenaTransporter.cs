using Godswar.Server.Application.Npc;
using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const string DuelArenaTransportUnavailableMessage =
        "Duel Arena transportation is temporarily unavailable.";

    private DuelArenaTransporterDialogueContext?
        _duelArenaTransporterDialogueContext;

    private async Task HandleDuelArenaTransporterAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !IsCanonicalDuelArenaTransporterAction(
                packet,
                route,
                npcId,
                dialogIndex) ||
            !TryAuthorizeDuelArenaTransporterAction(route, npcId) ||
            !TryResolveDuelArenaDestination(
                route,
                route.NpcKey,
                npcId,
                _character.CurrentMap,
                dialogIndex,
                subId,
                arguments,
                out var destination))
        {
            Console.Error.WriteLine(
                "[duel-arena-transporter] rejected malformed or " +
                $"unauthorized action character=" +
                $"{_character?.Name ?? "<none>"} npc={npcId} " +
                $"dialog={dialogIndex} sub={subId} length={packet.Length}");
            return;
        }

        ClearDuelArenaTransporterDialogueContext();
        // Editor override (npc_teleport_overrides): another point in the arena, or another map.
        var edited = NpcContentOverrides.Current.Teleports.GetValueOrDefault((route.NpcKey, subId));
        if (edited is not null && _character.Level < edited.MinimumLevel)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote($"You need level {edited.MinimumLevel} to go there."),
                cancellationToken,
                "DuelArenaTransporterLevelRequirement");
            return;
        }

        var outcome = edited is not null && edited.TargetMapId != _character.CurrentMap
            ? await TryBeginMapTransitionAsync(
                checked((byte)edited.TargetMapId),
                edited.X,
                edited.Z,
                $"npc-duel-arena-transporter-override:{route.NpcKey}:{subId}",
                cancellationToken)
            : await TryBeginSameMapSceneTransitionAsync(
                edited?.X ?? destination.TargetX,
                edited?.Z ?? destination.TargetZ,
                $"npc-duel-arena-transporter:{route.NpcKey}:{subId}",
                continuationGuard: null,
                cancellationToken);
        if (outcome == SceneTransitionOutcome.CommittedRequiresReconnect)
        {
            return;
        }

        if (outcome != SceneTransitionOutcome.CommittedAwaitingReadiness)
        {
            if (!_session.IsDisconnected)
            {
                await _session.SendAsync(
                    PacketBuilder.ServerNote(
                        DuelArenaTransportUnavailableMessage),
                    cancellationToken,
                    "DuelArenaTransporterUnavailable");
            }
            return;
        }

        Console.WriteLine(
            "[duel-arena-transporter] admitted " +
            $"character={_character.Name} npc={npcId} " +
            $"section={destination.Section} arrival=" +
            $"{destination.TargetX:F2},{destination.TargetZ:F2}");
    }

    private bool TryIssueDuelArenaTransporterDialogueContext(
        NpcSpawnDefinition npc)
    {
        ClearDuelArenaTransporterDialogueContext();
        if (_account is null ||
            _character is null ||
            _character.CurrentHp <= 0 ||
            _character.CurrentMap != DuelArenaTransporterProtocol.MapId ||
            !(DuelArenaCapturedTransportProtocol.IsCapturedEndpoint(
                npc.NpcKey, npc.InteractionId) ||
              DuelArenaTransporterProtocol.IsEndpoint(
                npc.NpcKey, npc.InteractionId) ||
              DuelArenaExitProtocol.IsEndpoint(npc.NpcKey, npc.InteractionId)) ||
            !IsWithinDuelArenaTransporterInteractionDistance(npc) ||
            !_registry.TryGetSessionWorldInstanceId(
                _session,
                out var worldInstanceId))
        {
            return false;
        }

        _duelArenaTransporterDialogueContext = new(
            _account.Id,
            _character.Id,
            npc.NpcKey,
            npc.InteractionId,
            _character.CurrentMap,
            worldInstanceId,
            DateTimeOffset.UtcNow +
                DuelArenaTransporterProtocol.MenuContextLifetime);
        return true;
    }

    private bool TryAuthorizeDuelArenaTransporterAction(
        NpcDialogueRouteDefinition route,
        uint npcId)
    {
        var context = _duelArenaTransporterDialogueContext;
        return context is not null &&
            _account is not null &&
            _character is not null &&
            _character.CurrentHp > 0 &&
            context.ExpiresAt > DateTimeOffset.UtcNow &&
            context.AccountId == _account.Id &&
            context.CharacterId == _character.Id &&
            context.SourceMapId == _character.CurrentMap &&
            context.NpcInteractionId == npcId &&
            string.Equals(
                context.NpcKey,
                route.NpcKey,
                StringComparison.Ordinal) &&
            TryResolveMapNpc(npcId, out var npc) &&
            IsWithinDuelArenaTransporterInteractionDistance(npc) &&
            _registry.TryGetSessionWorldInstanceId(
                _session,
                out var currentWorldInstanceId) &&
            currentWorldInstanceId == context.SourceWorldInstanceId;
    }

    private bool IsWithinDuelArenaTransporterInteractionDistance(
        NpcSpawnDefinition npc)
    {
        if (_character is null)
        {
            return false;
        }

        var deltaX = (double)_character.PositionX - npc.X;
        var deltaZ = (double)_character.PositionZ - npc.Z;
        var maximum = (double)
            DuelArenaTransporterProtocol.MaximumInteractionDistance;
        return double.IsFinite(deltaX) &&
            double.IsFinite(deltaZ) &&
            deltaX * deltaX + deltaZ * deltaZ <= maximum * maximum;
    }

    private void ClearDuelArenaTransporterDialogueContext() =>
        _duelArenaTransporterDialogueContext = null;

    private static bool IsCanonicalDuelArenaTransporterAction(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex) =>
        packet.Length == DuelArenaTransporterProtocol.ActionPacketBytes &&
        packet.Buffer.Length ==
            DuelArenaTransporterProtocol.ActionPacketBytes &&
        packet.Payload.Length >= 12 &&
        route.Behavior == NpcDialogueBehavior.DuelArenaTransporter &&
        route.DialogIndex == dialogIndex &&
        ((DuelArenaCapturedTransportProtocol.IsCapturedRoute(route) &&
            DuelArenaCapturedTransportProtocol.IsCapturedEndpoint(
                route.NpcKey, npcId)) ||
         (dialogIndex == DuelArenaTransporterProtocol.DialogIndex &&
            DuelArenaTransporterProtocol.IsEndpoint(route.NpcKey, npcId))) &&
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(int))) == dialogIndex;

    private static bool TryResolveDuelArenaDestination(
        NpcDialogueRouteDefinition route,
        string npcKey,
        uint npcId,
        byte mapId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out DuelArenaTransportDestination destination) =>
        DuelArenaCapturedTransportProtocol.IsCapturedRoute(route)
            ? DuelArenaCapturedTransportProtocol.TryResolveDestination(
                npcKey, npcId, mapId, dialogIndex, subId, arguments,
                out destination,
                entryIndex: npcId == DuelArenaCapturedLayout.GatekeeperNpcId
                    ? Random.Shared.Next(DuelArenaEntryDestinations.All.Count)
                    : 0)
            : DuelArenaTransporterProtocol.TryResolveDestination(
                npcKey, npcId, mapId, dialogIndex, subId, arguments,
                out destination);
}
