using Godswar.Server.Application.Rates;
using System.Buffers.Binary;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const string BattlefieldTransportUnavailableMessage =
        "Battlefield transportation is temporarily unavailable.";

    private BattlefieldTransporterDialogueContext?
        _battlefieldTransporterDialogueContext;

    private async Task HandleBattlefieldTransporterAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !IsCanonicalBattlefieldTransporterAction(
                packet,
                route,
                npcId,
                dialogIndex) ||
            !TryAuthorizeBattlefieldTransporterAction(route, npcId))
        {
            Console.Error.WriteLine(
                "[battlefield-transporter] rejected malformed or " +
                $"unauthorized action character=" +
                $"{_character?.Name ?? "<none>"} npc={npcId} " +
                $"dialog={dialogIndex} sub={subId} length={packet.Length}");
            return;
        }

        if (await TryHandleParnassusEntryAsync(npcId, dialogIndex, subId, arguments, cancellationToken)) // ARENA
        {
            return;
        }

        if (BattlefieldTransporterProtocol.TryGetNiMiniPage(
                dialogIndex,
                subId,
                arguments,
                out var pageSubIds))
        {
            _battlefieldTransporterDialogueContext =
                _battlefieldTransporterDialogueContext! with
                {
                    NiMiniPageIssued = true
                };
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    pageSubIds),
                cancellationToken,
                "BattlefieldTransporterNiMiniPage");
            return;
        }

        if (subId == BattlefieldTransporterProtocol.NiMiniRootSubId &&
            _battlefieldTransporterDialogueContext?.NiMiniPageIssued != true)
        {
            Console.Error.WriteLine(
                "[battlefield-transporter] rejected Ni Mini entry without " +
                $"current page context character={_character.Name} " +
                $"npc={npcId}");
            return;
        }

        if (!BattlefieldTransporterProtocol.TryResolveDestination(
                route.NpcKey,
                npcId,
                _character.CurrentMap,
                _character.Camp,
                dialogIndex,
                subId,
                arguments,
                out var destination))
        {
            Console.Error.WriteLine(
                "[battlefield-transporter] rejected unknown path " +
                $"character={_character.Name} npc={npcId} sub={subId}");
            return;
        }

        ClearBattlefieldTransporterDialogueContext();

        // BATTLEFIELD2: Pindus admits level 60-200 (battlefield.pindus_band; 0 = the compiled 31-120). An editor row
        // below still replaces the minimum, as it does for every destination.
        if (destination.Kind == BattlefieldDestinationKind.Pindus && BattlefieldRewardPolicy.PindusBandEnabled)
        {
            destination = destination with
            {
                MinimumLevel = BattlefieldRewardPolicy.PindusBandMinimumLevel,
                MaximumLevel = BattlefieldRewardPolicy.PindusBandMaximumLevel
            };
        }

        // Editor override (npc_teleport_overrides). Opening hours and the upper level limit stay as compiled.
        var edited = NpcContentOverrides.Current.Teleports.GetValueOrDefault((
            route.NpcKey,
            subId == BattlefieldTransporterProtocol.LelantineFarmSingleChannelSubId
                ? BattlefieldTransporterProtocol.LelantineFarmSubId
                : subId));
        if (edited is not null)
        {
            destination = BattlefieldTransporterProtocol.ApplyEdit(
                destination,
                edited.TargetMapId,
                edited.X,
                edited.Z,
                edited.MinimumLevel);
        }

        // FINALISE: editor level band (public.level_band_overrides), read hot.
        // FINALISE2: through the one gate rule set (Ni Mini is one gate, battlefield.nimini_upper, the admin F-67).
        var bandGate = BattlefieldSchedulePolicy.GateKey(destination.Kind);
        var band = Godswar.Server.Application.Gates.GateRules.Band(
            bandGate, destination.MinimumLevel, Math.Min(destination.MaximumLevel, Godswar.Server.Application.Gates.GateCatalog.LevelCap));
        destination = destination with { MinimumLevel = band.Minimum, MaximumLevel = band.Maximum };

        // GM accounts (the developer-command allowlist) skip the level and
        // opening-hours gates so a GM can use the transporter to test or run an event.
        // FINALISE: hot rate gm.obey_gates = 1 makes GMs obey them like everyone else.
        // FINALISE2: plus the gate's own GM rule (Gates table gm_bypass).
        var operatorBypass = Godswar.Server.Application.Gates.GateRules.GmMaySkip(bandGate) &&
            await HasOperatorAuthorityAsync(cancellationToken);
        if (!operatorBypass &&
            (_character.Level < destination.MinimumLevel ||
             _character.Level > destination.MaximumLevel))
        {
            await SendBattlefieldAdmissionResultAsync(
                npcId,
                destination.Kind,
                closed: false,
                cancellationToken);
            Console.WriteLine(
                "[battlefield-transporter] level gate rejected " +
                $"character={_character.Name} level={_character.Level} " +
                $"destination={destination.DisplayName} required=" +
                $"{destination.MinimumLevel}-{destination.MaximumLevel} gate={bandGate} " + // FINALISE2
                $"source={band.MinimumSource}/{band.MaximumSource}");
            return;
        }

        if (!operatorBypass && !BattlefieldSchedulePolicy.IsOpen(
                destination.Kind,
                _realmCalendar,
                DateTimeOffset.UtcNow))
        {
            await SendBattlefieldAdmissionResultAsync(
                npcId,
                destination.Kind,
                closed: true,
                cancellationToken);
            Console.WriteLine(
                "[battlefield-transporter] schedule gate rejected " +
                $"character={_character.Name} " +
                $"destination={destination.DisplayName} gate={bandGate} " + // FINALISE2
                $"mode={Godswar.Server.Application.Gates.GateRules.Mode(bandGate).ToString().ToLowerInvariant()}");
            return;
        }

        var transitioned = destination.TargetMapId is >= byte.MinValue and
                <= byte.MaxValue
            ? await TryBeginMapTransitionAsync(
                checked((byte)destination.TargetMapId),
                destination.TargetX,
                destination.TargetZ,
                $"npc-battlefield-transporter:{route.NpcKey}:{subId}",
                cancellationToken)
            : SceneTransitionOutcome.RejectedWithoutRelocation;
        // BATTLEFIELD: an admitted entry into an open Pindus / Ni Mini window makes the
        // character a participant of that round (IGG: every participant is rewarded).
        if (transitioned != SceneTransitionOutcome.RejectedWithoutRelocation)
        {
            await RecordBattlefieldEntryAsync(destination.Kind, CancellationToken.None);
        }
        if (transitioned == SceneTransitionOutcome.CommittedRequiresReconnect)
        {
            return;
        }
        if (transitioned == SceneTransitionOutcome.RejectedWithoutRelocation)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(
                    BattlefieldTransportUnavailableMessage),
                cancellationToken,
                "BattlefieldTransporterUnavailable");
            Console.Error.WriteLine(
                "[battlefield-transporter] transition rejected " +
                $"character={_character.Name} npc={npcId} sub={subId} " +
                $"target={destination.TargetMapId}");
            return;
        }

        Console.WriteLine(
            "[battlefield-transporter] admitted " +
            $"character={_character.Name} npc={npcId} sub={subId} " +
            $"destination={destination.DisplayName} " +
            $"map={destination.SourceMapId}->{destination.TargetMapId}");
    }

    private bool TryIssueBattlefieldTransporterDialogueContext(
        NpcSpawnDefinition npc)
    {
        ClearBattlefieldTransporterDialogueContext();
        if (_account is null ||
            _character is null ||
            _character.CurrentHp <= 0 ||
            !BattlefieldTransporterProtocol.IsEndpoint(
                npc.NpcKey,
                npc.InteractionId) ||
            !IsWithinBattlefieldTransporterInteractionDistance(npc) ||
            !_registry.TryGetSessionWorldInstanceId(
                _session,
                out var worldInstanceId))
        {
            return false;
        }

        _battlefieldTransporterDialogueContext =
            new BattlefieldTransporterDialogueContext(
                _account.Id,
                _character.Id,
                npc.NpcKey,
                npc.InteractionId,
                _character.CurrentMap,
                worldInstanceId,
                DateTimeOffset.UtcNow +
                    BattlefieldTransporterProtocol.MenuContextLifetime);
        return true;
    }

    private bool TryAuthorizeBattlefieldTransporterAction(
        NpcDialogueRouteDefinition route,
        uint npcId)
    {
        var context = _battlefieldTransporterDialogueContext;
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
            IsWithinBattlefieldTransporterInteractionDistance(npc) &&
            _registry.TryGetSessionWorldInstanceId(
                _session,
                out var currentWorldInstanceId) &&
            currentWorldInstanceId == context.SourceWorldInstanceId;
    }

    private bool IsWithinBattlefieldTransporterInteractionDistance(
        NpcSpawnDefinition npc)
    {
        if (_character is null)
        {
            return false;
        }

        var deltaX = (double)_character.PositionX - npc.X;
        var deltaZ = (double)_character.PositionZ - npc.Z;
        var maximum =
            (double)BattlefieldTransporterProtocol.MaximumInteractionDistance;
        return double.IsFinite(deltaX) &&
            double.IsFinite(deltaZ) &&
            deltaX * deltaX + deltaZ * deltaZ <= maximum * maximum;
    }

    private void ClearBattlefieldTransporterDialogueContext() =>
        _battlefieldTransporterDialogueContext = null;

    private async Task SendBattlefieldAdmissionResultAsync(
        uint npcId,
        BattlefieldDestinationKind destination,
        bool closed,
        CancellationToken cancellationToken)
    {
        var (dialogIndex, subId) = destination switch
        {
            // BATTLEFIELD2: the client's own Pindus lines in the same Index 2 branch of NpcFunTranmit.lua:
            // 7001 "You should be lv.60-140 ..." (the level refusal; client text edit to 60-200 in
            // Development/build/battlefield2/client) and 7002, which prints the IGG times, when those are the times.
            BattlefieldDestinationKind.Pindus when !closed && BattlefieldRewardPolicy.PindusBandEnabled =>
                (BattlefieldTransporterProtocol.PindusResultDialogIndex,
                    BattlefieldTransporterProtocol.PindusBandLevelResultSubId),
            BattlefieldDestinationKind.Pindus when closed && BattlefieldSchedulePolicy.PindusMatchesIggText() =>
                (BattlefieldTransporterProtocol.PindusResultDialogIndex,
                    BattlefieldTransporterProtocol.PindusIggClosedResultSubId),
            BattlefieldDestinationKind.Pindus when
                _character is { Level: < 31 } =>
                (BattlefieldTransporterProtocol
                    .PindusMinimumLevelResultDialogIndex,
                    BattlefieldTransporterProtocol
                        .PindusMinimumLevelResultSubId),
            BattlefieldDestinationKind.Pindus =>
                (BattlefieldTransporterProtocol.PindusResultDialogIndex,
                    closed
                        ? BattlefieldTransporterProtocol
                            .PindusClosedResultSubId
                        : BattlefieldTransporterProtocol
                            .PindusLevelResultSubId),
            BattlefieldDestinationKind.NiMiniLower or
            BattlefieldDestinationKind.NiMiniUpper =>
                (BattlefieldTransporterProtocol.NiMiniResultDialogIndex,
                    BattlefieldTransporterProtocol
                        .NiMiniUnavailableResultSubId),
            // The farm words its own refusals inside the activity window, so the
            // refusal is answered under dialog index 47 rather than the
            // transport menu's index 1.
            BattlefieldDestinationKind.LelantineFarm =>
                (BattlefieldTransporterProtocol.LelantineFarmResultDialogIndex,
                    closed
                        ? BattlefieldTransporterProtocol
                            .LelantineFarmClosedResultSubId
                        : BattlefieldTransporterProtocol
                            .LelantineFarmLevelResultSubId),
            _ => throw new InvalidOperationException(
                "The always-open Duel Arena has no admission result.")
        };
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                subId),
            cancellationToken,
            "BattlefieldTransporterAdmissionRejected");
    }

    private static bool IsCanonicalBattlefieldTransporterAction(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex) =>
        packet.Length == BattlefieldTransporterProtocol.ActionPacketBytes &&
        packet.Buffer.Length ==
            BattlefieldTransporterProtocol.ActionPacketBytes &&
        packet.Payload.Length >= 12 &&
        route.Behavior == NpcDialogueBehavior.BattlefieldTransporter &&
        route.DialogIndex == BattlefieldTransporterProtocol.DialogIndex &&
        BattlefieldTransporterProtocol.IsEndpoint(route.NpcKey, npcId) &&
        dialogIndex == BattlefieldTransporterProtocol.DialogIndex &&
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(int))) == dialogIndex;
}
