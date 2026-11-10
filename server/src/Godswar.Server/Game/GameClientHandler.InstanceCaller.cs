using Godswar.Server.Application.Npc;
using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IMedusaDailyEntryClaimStore? _medusaDailyEntries;
    private readonly ILegacyInstanceDailyEntryClaimStore?
        _legacyInstanceDailyEntries;
    private readonly ILegacyInstanceOpalPaymentStore?
        _legacyInstanceOpalPayments;
    private InstanceCallerPageContext? _instanceCallerPageContext;

    private async Task HandleInstanceCallerAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            ClearInstanceCallerPageContext();
            return;
        }

        // INSTANCES: a closed instance (server_rates switch off) is refused here,
        // before any page context, countdown, daily-entry claim or teleport.
        var closedInstance = InstanceCallerProtocol.ResolveClosedInstance(
            dialogIndex,
            subId,
            arguments,
            InstanceEntrySwitches.IsHarborAttackOpen,
            InstanceEntrySwitches.IsTrainingGroundOpen,
            InstanceEntrySwitches.IsWarriorTrainingOpen); // WARRIOR (F-51)
        if (closedInstance != InstanceCallerProtocol.ClosedInstance.None)
        {
            await RefuseClosedInstanceAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                closedInstance,
                cancellationToken);
            return;
        }

        if (InstanceCallerProtocol.TryGetPage(
                dialogIndex,
                subId,
                arguments,
                out var rootSubId,
                out var pageSubIds))
        {
            if (!IsCanonicalInstanceCallerAction(
                    packet,
                    route,
                    npcId,
                    dialogIndex))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected non-canonical page " +
                    $"request npc={npcId}");
                return;
            }

            if (!_registry.TryGetSessionWorldInstanceId(
                    _session,
                    out var sourceWorldInstanceId))
            {
                ClearInstanceCallerPageContext();
                return;
            }

            _instanceCallerPageContext = new InstanceCallerPageContext(
                _account.Id,
                _character.Id,
                route.NpcKey,
                npcId,
                dialogIndex,
                rootSubId,
                sourceWorldInstanceId,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow +
                    InstanceCallerProtocol.PageContextLifetime);
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    pageSubIds),
                cancellationToken,
                "InstanceCallerPage");
            return;
        }

        // 赫拉克里斯的试炼: the entry page admits the character instead of
        // answering with the reference's level-gate line. Checked before that
        // line's rule because both read the same argument word.
        if (InstanceCallerProtocol.TryResolveHeraclesTrialEntry(
                dialogIndex,
                arguments,
                out var heraclesMapId,
                out var heraclesX,
                out var heraclesZ))
        {
            if (!IsCanonicalInstanceCallerAction(
                    packet,
                    route,
                    npcId,
                    dialogIndex))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected non-canonical Heracles " +
                    $"entry request npc={npcId}");
                return;
            }

            var heraclesContext = _instanceCallerPageContext;
            ClearInstanceCallerPageContext();
            if (!IsCurrentInstanceCallerPageContext(
                    heraclesContext,
                    route,
                    npcId,
                    dialogIndex,
                    InstanceCallerProtocol.HeraclesTrialRootSubId))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected Heracles entry without " +
                    $"current page context npc={npcId}");
                return;
            }

            // TRAINING: the page opens the party's Enter window for one exact
            // Training Ground run (was: a direct move onto the shared map 210).
            await BeginTrainingGroundEntryAsync(npcId, dialogIndex, cancellationToken);

            return;
        }

        // WARRIOR (F-51): one of root 13's four ground buttons opens the
        // party's Enter window for that Warriors' Training Ground.
        if (InstanceCallerProtocol.TryResolveWarriorTrialEntry(
                dialogIndex,
                subId,
                arguments,
                out var warriorGround))
        {
            if (!IsCanonicalInstanceCallerAction(
                    packet,
                    route,
                    npcId,
                    dialogIndex))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected non-canonical Warriors' Training " +
                    $"Ground entry request npc={npcId}");
                return;
            }

            var warriorContext = _instanceCallerPageContext;
            ClearInstanceCallerPageContext();
            if (!IsCurrentInstanceCallerPageContext(
                    warriorContext,
                    route,
                    npcId,
                    dialogIndex,
                    InstanceCallerProtocol.WarriorTrialRootSubId))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected Warriors' Training Ground entry " +
                    $"without current page context npc={npcId}");
                return;
            }

            await BeginWarriorTrainingEntryAsync(warriorGround, npcId, dialogIndex, cancellationToken);
            return;
        }

        // 港湾遇袭: an eligible character is admitted by this page, while a
        // character below the level floor falls through to the shared level-gate
        // answer below - the exact line the reference answered this page with in
        // the September 28 capture.
        if (InstanceCallerProtocol.TryResolveHarborAttackEntry(
                dialogIndex,
                arguments) &&
            _character.Level >= LevelBandGates.Apply( // FINALISE: editor band
                LevelBandGates.HarborAttack,
                InstanceCallerProtocol.HarborAttackMinimumLevel,
                InstanceCallerProtocol.HarborAttackMaximumLevel).Minimum)
        {
            if (!IsCanonicalInstanceCallerAction(
                    packet,
                    route,
                    npcId,
                    dialogIndex))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected non-canonical 港湾遇袭 " +
                    $"entry request npc={npcId}");
                return;
            }

            var harborContext = _instanceCallerPageContext;
            ClearInstanceCallerPageContext();
            if (!IsCurrentInstanceCallerPageContext(
                    harborContext,
                    route,
                    npcId,
                    dialogIndex,
                    InstanceCallerProtocol.HarborAttackRootSubId))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected 港湾遇袭 entry without " +
                    $"current page context npc={npcId}");
                return;
            }

            // FINALISE2 (F-64): the Gates table's opening mode, at the page itself (before the Enter window).
            if (await RefuseClosedInstanceGateAsync(LevelBandGates.HarborAttack, "Bay Under Attack", cancellationToken))
            {
                return;
            }

            // Both harbor maps are the same dungeon at two level bands and the
            // client only sends the page number, so the admitted leader's own
            // level selects the content map.
            var harborDestination =
                InstanceCallerProtocol.ResolveHarborAttackDestination(
                    _character.Level);
            Console.WriteLine(
                "[instance-caller] 港湾遇袭 entry " +
                $"character={_character.Name} level={_character.Level} " +
                $"map={harborDestination.TargetMapId} " +
                $"scene={harborDestination.ClientSceneId}");
            await BeginLegacyInstanceEntryCountdownAsync(
                npcId,
                dialogIndex,
                harborDestination,
                cancellationToken);
            return;
        }

        if (InstanceCallerProtocol.TryGetLevelGateResult(
                dialogIndex,
                arguments,
                out var levelGateSubIds))
        {
            if (!IsCanonicalInstanceCallerAction(
                    packet,
                    route,
                    npcId,
                    dialogIndex))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected non-canonical level gate " +
                    $"request npc={npcId}");
                return;
            }

            ClearInstanceCallerPageContext();
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    levelGateSubIds),
                cancellationToken,
                "InstanceCallerLevelGate");
            return;
        }

        if (InstanceCallerProtocol.TryResolveEntry(
                dialogIndex,
                subId,
                arguments,
                out var destination))
        {
            if (!IsCanonicalInstanceCallerAction(
                    packet,
                    route,
                    npcId,
                    dialogIndex))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected non-canonical entry " +
                    $"request npc={npcId} destination={destination.Kind}");
                return;
            }

            var entryContext = _instanceCallerPageContext;
            ClearInstanceCallerPageContext();
            var expectedRootSubId = destination.Kind switch
            {
                InstanceCallerEntryKind.Atlantis =>
                    InstanceCallerProtocol.AtlantisRootSubId,
                InstanceCallerEntryKind.Wonderland =>
                    InstanceCallerProtocol.WonderlandRootSubId,
                _ => 0
            };
            if (!IsCurrentInstanceCallerPageContext(
                    entryContext,
                    route,
                    npcId,
                    dialogIndex,
                    expectedRootSubId))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected entry without current " +
                    $"page context npc={npcId} " +
                    $"destination={destination.Kind}");
                return;
            }

            await BeginLegacyInstanceEntryCountdownAsync(
                npcId,
                dialogIndex,
                destination,
                cancellationToken);
            return;
        }

        if (!InstanceCallerProtocol.TryResolveDifficulty(
                dialogIndex,
                subId,
                arguments,
                out var difficulty))
        {
            return;
        }

        if (!IsCanonicalInstanceCallerAction(
                packet,
                route,
                npcId,
                dialogIndex))
        {
            Console.Error.WriteLine(
                "[instance-caller] rejected non-canonical Medusa " +
                $"difficulty request npc={npcId}");
            return;
        }

        var context = _instanceCallerPageContext;
        ClearInstanceCallerPageContext();
        if (!IsCurrentInstanceCallerPageContext(
                context,
                route,
                npcId,
                dialogIndex,
                InstanceCallerProtocol.MedusaRootSubId))
        {
            Console.Error.WriteLine(
                "[instance-caller] rejected Medusa difficulty without " +
                $"current page context npc={npcId} difficulty={difficulty}");
            return;
        }

        if (!_registry.CanInitiateInstance(_session))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    InstanceCallerProtocol.QueueUnavailableResultSubId),
                cancellationToken,
                "InstanceCallerPartyLeaderRequired");
            Console.WriteLine(
                "[instance-caller] rejected non-leader instance " +
                $"request character={_character.Name}");
            return;
        }

        var partyStatus = _registry.TryCaptureMedusaParty(
            _session,
            out var party);
        if (partyStatus != MedusaPartyEntryStatus.Ready)
        {
            await SendInstanceCallerFailureAsync(
                npcId,
                dialogIndex,
                partyStatus,
                cancellationToken);
            return;
        }

        var encounterDifficulty = difficulty switch
        {
            InstanceCallerDifficulty.Advanced =>
                MedusaEncounterDifficulty.Enhanced,
            InstanceCallerDifficulty.Normal =>
                MedusaEncounterDifficulty.Normal,
            InstanceCallerDifficulty.Mythic =>
                MedusaEncounterDifficulty.Mythic,
            _ => throw new InvalidOperationException(
                $"Unsupported Instance Caller difficulty {difficulty}.")
        };
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                encounterDifficulty,
                out var encounter) ||
            !encounter.ContentMapId.TryGetLegacyValue(out var targetMapId) ||
            !MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "first-entry",
                out _))
        {
            await SendInstanceCallerFailureAsync(
                npcId,
                dialogIndex,
                MedusaPartyEntryStatus.RuntimeUnavailable,
                cancellationToken);
            return;
        }

        var entryStatus = await BeginMedusaLeaderEntryAsync(
            party,
            encounterDifficulty,
            targetMapId,
            cancellationToken);
        if (entryStatus != MedusaPartyEntryStatus.Ready)
        {
            await SendInstanceCallerFailureAsync(
                npcId,
                dialogIndex,
                entryStatus,
                cancellationToken);
        }
    }

    private async Task RefuseClosedInstanceAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        InstanceCallerProtocol.ClosedInstance closedInstance,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalInstanceCallerAction(
                packet,
                route,
                npcId,
                dialogIndex))
        {
            Console.Error.WriteLine(
                "[instance-caller] rejected non-canonical closed-instance " +
                $"request npc={npcId} instance={closedInstance}");
            return;
        }

        ClearInstanceCallerPageContext();
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                InstanceCallerProtocol.ClosedInstanceResultSubId),
            cancellationToken,
            "InstanceCallerClosedInstance");
        var note = closedInstance switch
        {
            InstanceCallerProtocol.ClosedInstance.HarborAttack =>
                InstanceEntrySwitches.HarborAttackClosedNote,
            InstanceCallerProtocol.ClosedInstance.WarriorTraining => // WARRIOR (F-51)
                InstanceEntrySwitches.WarriorTrainingClosedNote,
            _ => InstanceEntrySwitches.TrainingGroundClosedNote
        };
        await _session.SendAsync(
            PacketBuilder.PersonalNotice(
                System.Text.Encoding.ASCII.GetBytes(note)),
            cancellationToken,
            "InstanceCallerClosedInstanceNote");
        Console.WriteLine(
            "[instance-caller] closed instance refused " +
            $"character={_character?.Name ?? "<none>"} " +
            $"instance={closedInstance}");
    }

    private async Task SendInstanceCallerFailureAsync(
        uint npcId,
        int dialogIndex,
        MedusaPartyEntryStatus status,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                InstanceCallerProtocol.QueueUnavailableResultSubId),
            cancellationToken,
            "InstanceCallerAdmissionRejected");
        Console.WriteLine(
            "[instance-caller] Medusa admission rejected " +
            $"character={_character?.Name ?? "<none>"} status={status}");
    }

    private bool IsCurrentInstanceCallerPageContext(
        InstanceCallerPageContext? context,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int rootSubId) =>
        context is not null &&
        _account is not null &&
        _character is not null &&
        context.ExpiresAt > DateTimeOffset.UtcNow &&
        context.AccountId == _account.Id &&
        context.CharacterId == _character.Id &&
        _character.AccountId == _account.Id &&
        context.NpcInteractionId == npcId &&
        context.DialogIndex == dialogIndex &&
        context.RootSubId == rootSubId &&
        string.Equals(
            context.NpcKey,
            route.NpcKey,
            StringComparison.Ordinal) &&
        _registry.TryGetSessionWorldInstanceId(
            _session,
            out var currentWorldInstanceId) &&
        currentWorldInstanceId == context.SourceWorldInstanceId;

    private static bool IsCanonicalInstanceCallerAction(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex) =>
        packet.Length == InstanceCallerProtocol.ActionPacketBytes &&
        packet.Buffer.Length == InstanceCallerProtocol.ActionPacketBytes &&
        route.Behavior == NpcDialogueBehavior.InstanceCaller &&
        route.DialogIndex == InstanceCallerProtocol.DialogIndex &&
        InstanceCallerProtocol.IsEndpoint(route.NpcKey, npcId) &&
        dialogIndex == InstanceCallerProtocol.DialogIndex &&
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(int))) == dialogIndex;

    private void ClearInstanceCallerPageContext() =>
        _instanceCallerPageContext = null;

    /// <summary>FINALISE2 (F-64): true (and a note sent) when an instance's gate is closed now (Gates table mode/windows).</summary>
    private async Task<bool> RefuseClosedInstanceGateAsync(string gateKey, string name, CancellationToken cancellationToken)
    {
        if (Godswar.Server.Application.Gates.GateSchedules.IsOpen(gateKey, _realmCalendar, DateTimeOffset.UtcNow))
        {
            return false;
        }

        await _session.SendAsync(
            PacketBuilder.ServerNote($"{name} is closed now. Check the Event window for its opening times."),
            cancellationToken,
            "InstanceGateClosed");
        Console.WriteLine(
            $"[instance-caller] schedule gate rejected character={_character?.Name} gate={gateKey} " +
            $"mode={Godswar.Server.Application.Gates.GateRules.Mode(gateKey).ToString().ToLowerInvariant()}");
        return true;
    }
}
