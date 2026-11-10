using Godswar.Server.Application.Gates; // FINALISE2
using Godswar.Server.Application.Npc;
using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const string TransporterUnavailableMessage =
        "Transportation is temporarily unavailable. Please try again.";

    private TransporterDialogueContext? _transporterDialogueContext;

    private async Task HandleTransporterAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !IsCanonicalTransporterAction(
                packet,
                route,
                npcId,
                dialogIndex) ||
            !TryAuthorizeTransporterAction(route, npcId) ||
            !TransporterProtocol.TryResolveDestination(
                route.NpcKey,
                npcId,
                _character.CurrentMap,
                dialogIndex,
                subId,
                arguments,
                out var destination))
        {
            Console.Error.WriteLine(
                "[transporter] rejected malformed or unauthorized action " +
                $"character={_character?.Name ?? "<none>"} npc={npcId} " +
                $"dialog={dialogIndex} sub={subId} length={packet.Length}");
            return;
        }

        ClearTransporterDialogueContext();

        // Editor override (npc_teleport_overrides): another map, point or level for this button.
        var teleportOverride = NpcContentOverrides.Current.Teleports.GetValueOrDefault((route.NpcKey, subId));
        // FINALISE2 (the admin F-18): the gate of the destination (level band, opening mode, GM rule). Precedence:
        // a Gates row, else the button's npc_teleport_overrides minimum, else the compiled minimum. No cap unless edited.
        var gateKey = GateCatalog.TransporterKey(destination.TargetMapId);
        var gate = gateKey is null
            ? new GateBand(teleportOverride?.MinimumLevel ?? destination.MinimumLevel, GateCatalog.LevelCap, "compiled", "compiled")
            : GateRules.Band(
                gateKey,
                teleportOverride?.MinimumLevel ?? destination.MinimumLevel,
                GateCatalog.LevelCap,
                teleportOverride is null ? "compiled" : "npc_teleport_overrides");
        var gmSkips = gateKey is not null && GateRules.GmMaySkip(gateKey) &&
            await HasOperatorAuthorityAsync(cancellationToken);
        if (!gmSkips && (_character.Level < gate.Minimum || _character.Level > gate.Maximum))
        {
            await _session.SendAsync(
                PacketBuilder.CapturedNpcFunctionActionResponse(
                    npcId,
                    TransporterProtocol.ResultDialogIndex,
                    destination.LevelRequirementResultSubId),
                cancellationToken,
                "TransporterLevelRequirement");
            // FINALISE2 (the admin F-80): log the level actually enforced (editor row or button override), not the compiled one.
            Console.WriteLine(
                "[transporter] level gate rejected " +
                $"character={_character.Name} level={_character.Level} " +
                $"destination={destination.DisplayName} gate={gateKey ?? "-"} " +
                $"required={gate.Minimum}-{gate.Maximum} source={gate.MinimumSource}/{gate.MaximumSource} " +
                $"mode={(gateKey is null ? "-" : GateRules.Mode(gateKey).ToString().ToLowerInvariant())}");
            return;
        }

        if (!gmSkips && gateKey is not null &&
            !GateSchedules.IsOpen(gateKey, _realmCalendar, DateTimeOffset.UtcNow))
        {
            // Mycenae's own line (NF_L0_10004 "Mycenae opens from 12:00 - 23:55.", NpcFunTranmit Index 1 sub 10001)
            // while its compiled hours apply; any other closed gate has no client line, so a server note.
            var mycenaeLine = gateKey == GateKeys.Mycenae && GateRules.Mode(gateKey) == GateSchedule.Compiled &&
                GateRules.EditedWindows(gateKey) is null;
            await _session.SendAsync(
                mycenaeLine
                    ? PacketBuilder.CapturedNpcFunctionActionResponse(npcId, TransporterProtocol.DialogIndex, TransporterProtocol.MycenaeClosedResultSubId)
                    : PacketBuilder.ServerNote($"{destination.DisplayName} is closed now."),
                cancellationToken,
                "TransporterClosed");
            Console.WriteLine(
                "[transporter] schedule gate rejected " +
                $"character={_character.Name} destination={destination.DisplayName} gate={gateKey} " +
                $"mode={GateRules.Mode(gateKey).ToString().ToLowerInvariant()} realm={_realmCalendar.ToRealmTime(DateTimeOffset.UtcNow):ddd HH:mm}");
            return;
        }

        // FINALISE2 (the admin F-19): the capitals' Mycenae button costs 20,000 Silver (NF_L0_10003). GMs who skip the
        // gate do not pay. The debit is atomic and refunded if the move does not happen.
        var silverFee = !gmSkips && gateKey == GateKeys.Mycenae && destination.TargetMapId == 6
            ? GateCatalog.MycenaeSilverFee
            : 0;
        if (silverFee > 0)
        {
            var charged = await _store.ChargeSilverAsync(_account!.Id, _character.Id, silverFee, cancellationToken);
            if (charged is null)
            {
                await _session.SendAsync(
                    PacketBuilder.CapturedNpcFunctionActionResponse(npcId, TransporterProtocol.DialogIndex, TransporterProtocol.MycenaeSilverResultSubId),
                    cancellationToken,
                    "TransporterSilverRequirement");
                Console.WriteLine(
                    $"[transporter] silver gate rejected character={_character.Name} silver={_character.Silver} fee={silverFee}");
                return;
            }

            _character.Silver = charged.Silver;
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "TransporterSilverCharged");
            Console.WriteLine(
                $"[transporter] silver charged character={_character.Name} fee={silverFee} wallet={charged.Silver}");
        }

        try
        {
            await ContinueTransporterAsync(route, npcId, subId, destination, teleportOverride, silverFee, cancellationToken);
        }
        catch when (silverFee > 0)
        {
            await RefundTransporterSilverAsync(silverFee, "fault");
            throw;
        }
    }

    // FINALISE2: the move itself, split out so the Mycenae fee can be refunded when the move does not happen.
    private async Task ContinueTransporterAsync(
        NpcDialogueRouteDefinition route,
        uint npcId,
        int subId,
        TransporterDestination destination,
        NpcTeleportOverride? teleportOverride,
        int silverFee,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (teleportOverride is not null)
        {
            var overridden = await TryBeginMapTransitionAsync(
                checked((byte)teleportOverride.TargetMapId),
                teleportOverride.X,
                teleportOverride.Z,
                $"npc-transporter-override:{route.NpcKey}:{subId}",
                cancellationToken);
            if (overridden is not SceneTransitionOutcome.CommittedAwaitingReadiness and not SceneTransitionOutcome.CommittedRequiresReconnect)
            {
                await RefundTransporterSilverAsync(silverFee, "override-rejected"); // FINALISE2
                await SendTransporterUnavailableAsync(cancellationToken);
            }

            return;
        }

        if (!_gameplayCatalogs.MapTraversal.TryGetAutomaticLink(
                destination.ArrivalAnchorSourceMapId,
                destination.TargetMapId,
                out var entryAnchor) ||
            !_gameplayCatalogs.MapTraversal.TryResolveTargetArrival(
                entryAnchor,
                MapPortalTriggerRadius,
                out var arrival) ||
            destination.TargetMapId is < byte.MinValue or > byte.MaxValue)
        {
            Console.Error.WriteLine(
                "[transporter] reviewed destination has no safe arrival " +
                $"npc={npcId} sub={subId} " +
                $"anchor={destination.ArrivalAnchorSourceMapId}->" +
                $"{destination.TargetMapId}");
            await RefundTransporterSilverAsync(silverFee, "no-arrival"); // FINALISE2
            await SendTransporterUnavailableAsync(cancellationToken);
            return;
        }

        // FINALISE2 F-08: a transporter into a north map lands inside that map's camp safe zone.
        var arrivalX = arrival.TargetArrival.X; // FINALISE2
        var arrivalZ = arrival.TargetArrival.Z; // FINALISE2
        if (Godswar.Server.Application.North.NorthCampArrival.TryGet(destination.TargetMapId, out var campX, out var campZ)) // FINALISE2
        {
            arrivalX = campX;
            arrivalZ = campZ;
        }

        var transitioned = await TryBeginMapTransitionAsync(
            checked((byte)destination.TargetMapId),
            arrivalX,
            arrivalZ,
            $"npc-transporter:{route.NpcKey}:{subId}",
            cancellationToken);
        if (transitioned != SceneTransitionOutcome.CommittedAwaitingReadiness)
        {
            if (transitioned == SceneTransitionOutcome.CommittedRequiresReconnect)
            {
                return;
            }
            Console.Error.WriteLine(
                "[transporter] transition rejected by map authority " +
                $"character={_character.Name} npc={npcId} sub={subId} " +
                $"target={destination.TargetMapId}");
            await RefundTransporterSilverAsync(silverFee, "transition-rejected"); // FINALISE2
            await SendTransporterUnavailableAsync(cancellationToken);
            return;
        }

        Console.WriteLine(
            "[transporter] admitted " +
            $"character={_character.Name} npc={npcId} sub={subId} " +
            $"destination={destination.DisplayName} " +
            $"map={destination.SourceMapId}->{destination.TargetMapId}");
    }

    // FINALISE2 (F-19): give the Mycenae fee back when the move did not happen.
    private async Task RefundTransporterSilverAsync(int silverFee, string reason)
    {
        if (silverFee <= 0 || _account is null || _character is null)
        {
            return;
        }

        var wallet = await _store.GrantQuestCurrencyAsync(_account.Id, _character.Id, silverFee, 0, CancellationToken.None);
        if (wallet is not null)
        {
            _character.Silver = wallet.Silver;
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            if (!_session.IsDisconnected)
            {
                await _session.SendAsync(BuildLocalPlayerStatusUpdate(), CancellationToken.None, "TransporterSilverRefund");
            }
        }

        Console.WriteLine($"[transporter] silver refunded character={_character.Name} fee={silverFee} reason={reason}");
    }

    private bool TryIssueTransporterDialogueContext(
        NpcSpawnDefinition npc)
    {
        ClearTransporterDialogueContext();
        if (_account is null ||
            _character is null ||
            _character.CurrentHp <= 0 ||
            !TransporterProtocol.IsEndpoint(
                npc.NpcKey,
                npc.InteractionId) ||
            !IsWithinTransporterInteractionDistance(npc) ||
            !_registry.TryGetSessionWorldInstanceId(
                _session,
                out var worldInstanceId))
        {
            return false;
        }

        _transporterDialogueContext = new TransporterDialogueContext(
            _account.Id,
            _character.Id,
            npc.NpcKey,
            npc.InteractionId,
            _character.CurrentMap,
            worldInstanceId,
            DateTimeOffset.UtcNow +
                TransporterProtocol.MenuContextLifetime);
        return true;
    }

    private bool TryAuthorizeTransporterAction(
        NpcDialogueRouteDefinition route,
        uint npcId)
    {
        var context = _transporterDialogueContext;
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
            IsWithinTransporterInteractionDistance(npc) &&
            _registry.TryGetSessionWorldInstanceId(
                _session,
                out var currentWorldInstanceId) &&
            currentWorldInstanceId == context.SourceWorldInstanceId;
    }

    private bool IsWithinTransporterInteractionDistance(
        NpcSpawnDefinition npc)
    {
        if (_character is null)
        {
            return false;
        }

        var deltaX = (double)_character.PositionX - npc.X;
        var deltaZ = (double)_character.PositionZ - npc.Z;
        var maximum =
            (double)TransporterProtocol.MaximumInteractionDistance;
        return double.IsFinite(deltaX) &&
            double.IsFinite(deltaZ) &&
            deltaX * deltaX + deltaZ * deltaZ <= maximum * maximum;
    }

    private void ClearTransporterDialogueContext() =>
        _transporterDialogueContext = null;

    private async Task SendTransporterUnavailableAsync(
        CancellationToken cancellationToken) =>
        await _session.SendAsync(
            PacketBuilder.ServerNote(TransporterUnavailableMessage),
            cancellationToken,
            "TransporterUnavailable");

    private static bool IsCanonicalTransporterAction(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex) =>
        packet.Length == TransporterProtocol.ActionPacketBytes &&
        packet.Buffer.Length == TransporterProtocol.ActionPacketBytes &&
        packet.Payload.Length >= 12 &&
        route.Behavior == NpcDialogueBehavior.Transporter &&
        route.DialogIndex == TransporterProtocol.DialogIndex &&
        TransporterProtocol.IsEndpoint(route.NpcKey, npcId) &&
        dialogIndex == TransporterProtocol.DialogIndex &&
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(int))) == dialogIndex;
}
