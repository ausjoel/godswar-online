using Godswar.Server.Application.Finalise2;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// PROF slice 2 (2026-11-09): gathering through the client's own windows. Hot switch profession.gathering (default 1).
//  1. The capital Event Transporter's 600 "*To Romance Island" moves a level 70+ character to map 45 (3601 below 70).
//  2. A node (Mine Vein / Hunting Range / Nifty Garden, CollNpc.ini) opens with function 65 (NpcFunGather.lua):
//     page 1 = its description, the tool slot (105) and "*Resource Gathering" (100). The 100 click carries the
//     placed tool in argument slot 6; the server answers 905 (the script calls GameAPI:Collction(), the gather
//     window) or 900 (tool level), 901-904 (wrong tool), 906 (bag full).
//  3. The gather window sends 10344 state 1 on Start and again every 2 s; each one is one attempt answered with a
//     10345: 0 + the item (and a kit-bag refresh), 2 for a miss or an early tick, or 1/3/4/6 which stop the window.
//     A stop (state 0) gets no answer: the client sends one after every error, and an answer would loop.
//  4. 10342 (the gather rabbit on/off) is relayed to the other players on the map with this player's object id.
// Every write runs in one EventsNpc transaction with the character row locked. No pop-up follows (the admin's rule);
// stacks never go above 99. GENERATED values are in State/GatheringTables.cs. Evidence: docs/professions/README.md s.6.
internal sealed partial class GameClientHandler
{
    private static bool GatheringOn => Finalise2Rates.On(Finalise2RateKeys.Gathering);

    /// <summary>The node and tool the last 905 opened the gather window for.</summary>
    private sealed record GatherSessionState(GatherNode Node, uint NodeInteractionId, float NodeX, float NodeZ,
        uint ToolId, int Tier, DateTimeOffset LastAttempt);

    private GatherSessionState? _gatherSession;

    // ---- the Event Transporter and the island's transporters ---------------------------------------------------

    private async Task<bool> TryHandleRomanceIslandActionAsync(NpcSpawnDefinition npc, int dialogIndex, int subId,
        CancellationToken cancellationToken)
    {
        if (RomanceIslandProtocol.IsEntryClick(npc.NpcKey, dialogIndex, subId))
        {
            await HandleRomanceIslandEntryAsync(npc, cancellationToken);
            return true;
        }

        if (!RomanceIslandProtocol.IsEnabled || !RomanceIslandProtocol.IsIslandNpc(npc))
        {
            return false;
        }

        if (RomanceIslandProtocol.IsHomeTransporter(npc) &&
            dialogIndex == CursedLandTransportProtocol.HomeTeleportFunction && subId == -1)
        {
            await HandleRomanceIslandHomeAsync(npc, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task HandleRomanceIslandEntryAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }

        if (_character.CurrentMap == RomanceIslandProtocol.MapId || _pendingMapTransition is not null ||
            !_registered || !_worldPresenceAnnounced)
        {
            Console.WriteLine($"[gather] entry ignored character={_character.Name} map={_character.CurrentMap}");
            return;
        }

        var gmSkips = await HasOperatorAuthorityAsync(cancellationToken);
        if (!gmSkips && _character.Level < GatheringTables.MinimumLevel)
        {
            await _session.SendAsync(PacketBuilder.NpcFunctionActionResponse(npc.InteractionId,
                CursedLandTransportProtocol.EventTransporterDialogIndex, RomanceIslandProtocol.LevelResultSubId),
                cancellationToken, "RomanceIslandRefused");
            Console.WriteLine($"[gather] entry refused character={_character.Name} level={_character.Level}");
            return;
        }

        var (x, z) = RomanceIslandProtocol.Arrival;
        var outcome = await TryBeginMapTransitionCoreAsync(RomanceIslandProtocol.MapId, x, z,
            $"romance-island:{npc.NpcKey}", cancellationToken, publishFightStateReset: true);
        Console.WriteLine($"[gather] entry character={_character.Name} level={_character.Level} " +
            $"map={RomanceIslandProtocol.MapId} arrival={x},{z} outcome={outcome}");
    }

    private async Task HandleRomanceIslandHomeAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        if (_character is null || _character.CurrentMap != RomanceIslandProtocol.MapId)
        {
            return;
        }

        _gatherSession = null;
        // The captured Cursed Land home landing (Athens 20,-100) and the reviewed Sparta capital point, as the
        // Pan's Labyrinth home transporter uses them.
        var sparta = _character.Camp == GameDefaults.SpartaCamp;
        var targetMapId = sparta ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
        var targetX = sparta ? GameDefaults.StartingPositionX : CursedLandTransportProtocol.AthensHome.X;
        var targetZ = sparta ? GameDefaults.StartingPositionZ : CursedLandTransportProtocol.AthensHome.Z;
        var outcome = await TryBeginMapTransitionCoreAsync(targetMapId, targetX, targetZ,
            $"romance-island-home:{npc.NpcKey}", cancellationToken, publishFightStateReset: true);
        Console.WriteLine($"[gather] home transport character={_character.Name} map={targetMapId} outcome={outcome}");
    }

    /// <summary>
    /// A character saved on map 45 while profession.gathering is 0 logs in at the capital, so switching gathering
    /// off never strands anyone on an island without actors (as Pan's Labyrinth does for map 40).
    /// </summary>
    private bool TryRecoverRomanceIslandLogin()
    {
        if (_character is null || _character.CurrentMap != RomanceIslandProtocol.MapId || RomanceIslandProtocol.IsEnabled)
        {
            return false;
        }

        GameDefaults.InitializeStartingLocation(_character);
        _unavailableInstanceReconnectMap = RomanceIslandProtocol.MapId;
        _positionDirty = true;
        Console.WriteLine($"[gather] switch off: saved map 45 recovered to capital character={_character.Name} " +
            $"map={_character.CurrentMap}");
        return true;
    }

    /// <summary>The island actors' open frames: nodes = function 65, Main City Transporter = function 97.</summary>
    private async Task<bool> TryHandleRomanceIslandNpcOpenAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        if (!RomanceIslandProtocol.IsEnabled || !RomanceIslandProtocol.IsIslandNpc(npc))
        {
            return false;
        }

        if (RomanceIslandProtocol.IsHomeTransporter(npc))
        {
            await _session.SendAsync(PacketBuilder.NpcDialogOpenAck(npc.InteractionId,
                [CursedLandTransportProtocol.HomeTeleportFunction], npc.NpcKey), cancellationToken, "RomanceIslandHomeOpen");
            return true;
        }

        if (GatheringTables.TryGetNodeByNpcKey(npc.NpcKey, out _))
        {
            // Flags 0x200 + function 65: the client's 0x5acf03 path also matches the entity name with CollNpc.ini
            // and stores the node id the gather window sends.
            await _session.SendAsync(PacketBuilder.NpcDialogOpenAck(npc.InteractionId,
                [GatheringTables.GatherFunction], npc.NpcKey), cancellationToken, "GatherNodeOpen");
            Console.WriteLine($"[gather] node open character={_character?.Name} npc={npc.NpcKey}");
            return true;
        }

        // The Gathering Node Transporter (010): its description (its teleport buttons have no client script page).
        await SendNpcDescriptionOpenAsync(npc, cancellationToken);
        return true;
    }

    // ---- the node's dialogue (function 65) ----------------------------------------------------------------------

    private async Task<bool> TryHandleGatherNodeActionAsync(NpcSpawnDefinition npc, int dialogIndex, int subId,
        GamePacket packet, CancellationToken cancellationToken)
    {
        if (!GatheringOn || dialogIndex != GatheringTables.GatherFunction || _character is null ||
            !GatheringTables.TryGetNodeByNpcKey(npc.NpcKey, out var node))
        {
            return false;
        }

        var args = MountFeederArguments(packet);
        var selection = ResolveNpcFunctionSelection(subId, args[..(NpcFunctionClickSlotCount - 1)]);
        var placed = SlotOf(args[NpcFunctionClickSlotCount - 1]);
        Console.WriteLine($"[gather] node action character={_character.Name} npc={npc.NpcKey} node={node.Id} " +
            $"sub={subId} selection={selection} args=[{string.Join(',', args)}]");
        if (selection != GatheringTables.GatherButton)
        {
            // Page 1: the node's description, the tool slot and the gather button.
            await SendEventsNpcReplyAsync(npc, GatheringTables.GatherFunction,
                [GatheringTables.DescriptionLine(node), GatheringTables.PlaceToolText, GatheringTables.GatherButton],
                "GatherNodePage", cancellationToken);
            return true;
        }

        var item = placed >= 0 ? KitBagSlots.GetItem(_character.KitBag, placed) : default;
        var tier = placed >= 0 && !item.IsEmpty ? GatheringTables.ToolTier(node.Kind, item.Id) : -1;
        int reply;
        if (tier < 0)
        {
            reply = GatheringTables.WrongToolLine(node);
        }
        else if (_character.Level < GatheringTables.ToolLevel(tier))
        {
            reply = GatheringTables.ToolLevelTooLow;
        }
        else if (!HasEmptyBagSlot())
        {
            reply = GatheringTables.BagFull;
        }
        else
        {
            RomanceIslandProtocol.TryGetPlacement(npc.NpcKey, out var nodeX, out var nodeZ);
            _gatherSession = new GatherSessionState(node, npc.InteractionId, nodeX, nodeZ, item.Id, tier,
                DateTimeOffset.MinValue);
            reply = GatheringTables.OpenGatherWindow;
        }

        Console.WriteLine($"[gather] confirm character={_character.Name} node={node.Id} slot={placed} " +
            $"tool={(item.IsEmpty ? 0 : item.Id)} tier={tier} reply={reply}");
        await SendEventsNpcReplyAsync(npc, GatheringTables.GatherFunction, [reply], "GatherNodeConfirm", cancellationToken);
        return true;
    }

    private bool HasEmptyBagSlot() =>
        _character is { } character &&
        Enumerable.Range(0, 96).Any(slot => KitBagSlots.GetItem(character.KitBag, slot).IsEmpty);

    // ---- the gather window (10342 / 10344) ----------------------------------------------------------------------

    private async Task<bool> TryHandleGatheringPacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (packet.Opcode is not (PacketBuilder.GatherPoseOpcode or PacketBuilder.GatherRequestOpcode))
        {
            return false;
        }

        // Rig rule: no S2C before EnterUiReady (10357). Gather frames only ever answer the gather window's own
        // requests, which the client sends from its world UI; the registry's UI-ready mark (cleared by every map change
        // until the next 10357) guards a stray frame during entry anyway.
        if (_character is null || !_enterUiReadyReceived || !_registry.IsProfessionUiReady(_session))
        {
            return true;
        }

        if (packet.Opcode == PacketBuilder.GatherPoseOpcode)
        {
            if (GatheringOn && PacketBuilder.TryReadGatherPose(packet.Buffer, out var pose) &&
                pose.State is 0 or 1 && GatheringTables.TryGetNode(pose.NodeId, out _))
            {
                await _registry.RelayGatherPoseAsync(_session,
                    PacketBuilder.GatherPoseFrame(pose.State, pose.NodeId, CurrentPlayerObjectId), cancellationToken);
            }

            return true;
        }

        if (!PacketBuilder.TryReadGatherRequest(packet.Buffer, out var request))
        {
            return true;
        }

        if (!request.Gathering)
        {
            Console.WriteLine($"[gather] stop character={_character.Name} node={request.NodeId} tool-word={request.ToolWord}");
            return true;
        }

        try
        {
            await GatherOnceAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[gather] fault character={_character.Name} node={request.NodeId}: " +
                $"{error.GetType().Name}: {error.Message}");
            await _session.SendAsync(PacketBuilder.GatherResult(PacketBuilder.GatherError), cancellationToken, "GatherResult");
        }

        return true;
    }

    private sealed record GatherOutcome(int Result, GatherRoll Roll, int Ap, bool Rolled);

    private async Task GatherOnceAsync(PacketBuilder.GatherRequest request, CancellationToken cancellationToken)
    {
        var character = _character!;
        var state = _gatherSession;
        var now = DateTimeOffset.UtcNow;
        int early = 0;
        if (!GatheringOn || state is null || state.Node.Id != request.NodeId ||
            character.CurrentMap != RomanceIslandProtocol.MapId ||
            !GatheringTables.InReach(character.PositionX, character.PositionZ, state.NodeX, state.NodeZ))
        {
            early = PacketBuilder.GatherIllegal;
        }
        else if (now - state.LastAttempt < GatheringTables.MinimumInterval)
        {
            early = PacketBuilder.GatherNothing;
        }
        else if (!HasEmptyBagSlot())
        {
            // The client puts a gathered item in its first EMPTY slot (0x4ac810), so a free slot is required here too.
            early = PacketBuilder.GatherBagFull;
        }

        if (early != 0)
        {
            Console.WriteLine($"[gather] attempt character={character.Name} node={request.NodeId} " +
                $"tool-word={request.ToolWord} result={early} (pre-check)");
            await _session.SendAsync(PacketBuilder.GatherResult(early), cancellationToken, "GatherResult");
            return;
        }

        _gatherSession = state! with { LastAttempt = now };
        // The slot the client will drop a gathered item into by itself (0x4ac810: its first empty slot).
        var clientInsertSlot = Enumerable.Range(0, 96).FirstOrDefault(
            slot => KitBagSlots.GetItem(character.KitBag, slot).IsEmpty, -1);
        var tier = state!.Tier;
        var roll = GatheringTables.Roll(state.Node.Kind, tier, Random.Shared.Next(100), Random.Shared.Next(100),
            Random.Shared.Next(100), Random.Shared.Next(tier + 1));
        var maxAp = ProfessionMaxAp;
        var outcome = await RunEventsNpcAsync(async tx =>
        {
            // GENERATED: a character without a profession row starts with a full AP bar, as a first profession does.
            var current = await ReadProfessionAsync(tx);
            if (current is null)
            {
                await tx.ScalarAsync("""
                    INSERT INTO public.profession_state (character_id, profession, level, experience, ap, ap_updated_at)
                    VALUES (@c, 0, 1, 0, @ap, now()) ON CONFLICT (character_id) DO NOTHING;
                    """, ("ap", maxAp));
                current = await ReadProfessionAsync(tx);
            }

            var ap = current?.Ap ?? 0;
            if (ap < GatheringTables.ApPerAttempt)
            {
                return new GatherOutcome(PacketBuilder.GatherNoAp, default, ap, false);
            }

            if (await tx.CountAsync((int)state.ToolId) <= 0)
            {
                return new GatherOutcome(PacketBuilder.GatherWrongTool, default, ap, false);
            }

            if (roll.Success)
            {
                // GENERATED: a success uses one tool; a miss costs only the AP.
                if (!await tx.ConsumeAsync((int)state.ToolId, 1))
                {
                    tx.Rollback();
                    return new GatherOutcome(PacketBuilder.GatherWrongTool, default, ap, false);
                }

                if (!await tx.GrantAsync((int)roll.ItemId, roll.Count, bound: false, stackCap: 99))
                {
                    tx.Rollback();
                    return new GatherOutcome(PacketBuilder.GatherBagFull, default, ap, false);
                }
            }

            ap -= GatheringTables.ApPerAttempt;
            await tx.ScalarAsync("UPDATE public.profession_state SET ap = @ap, updated_at = now() WHERE character_id = @c;",
                ("ap", ap));
            return new GatherOutcome(roll.Success ? PacketBuilder.GatherObtained : PacketBuilder.GatherNothing, roll, ap,
                true);
        }, new GatherOutcome(PacketBuilder.GatherError, default, 0, false), cancellationToken);

        Console.WriteLine($"[gather] attempt character={character.Name} node={state.Node.Id} kind={state.Node.Kind} " +
            $"tool={state.ToolId} tier={tier} tool-word={request.ToolWord} result={outcome.Result} " +
            $"item={outcome.Roll.ItemId}x{outcome.Roll.Count} ap={outcome.Ap}");

        // 10345 first: on 0 the client inserts the item into its first empty slot by itself, so the authoritative
        // kit-bag refresh must come after it and overwrite that guess (the server tops up stacks first).
        await _session.SendAsync(outcome.Result == PacketBuilder.GatherObtained
            ? PacketBuilder.GatherResult(PacketBuilder.GatherObtained, outcome.Roll.ItemId, outcome.Roll.Count)
            : PacketBuilder.GatherResult(outcome.Result), cancellationToken, "GatherResult");
        if (outcome.Result == PacketBuilder.GatherObtained)
        {
            // Clear the client's own insert first (the server may have topped up a stack instead), then the
            // authoritative bag pages put back whatever the server really holds in that slot.
            if (clientInsertSlot >= 0)
            {
                await _session.SendAsync(PacketBuilder.StorageItemKitBagDelete(clientInsertSlot), cancellationToken,
                    "GatherClientSlotClear");
            }

            await RefreshAfterEventsNpcAsync([], cancellationToken);
        }

        if (outcome.Rolled || outcome.Result == PacketBuilder.GatherNoAp)
        {
            await _session.SendAsync(PacketBuilder.SyncActivityPoints(outcome.Ap, maxAp), cancellationToken, "ProfessionAp");
        }
    }
}
