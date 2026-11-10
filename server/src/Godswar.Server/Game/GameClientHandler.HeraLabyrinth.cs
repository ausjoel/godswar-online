using Godswar.Server.Application.Gates;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.State;
using H = Godswar.Server.Domain.World.Content.HeraLabyrinthProtocol;

namespace Godswar.Server.Game;

/// <summary>
/// HERA'S LABYRINTH (the admin F-161): the Event Transporter's 500 / 501, the maze portals on maps 30 / 31, Zeus' Envoy
/// (006) and Zeus' Envoys 1-5. GODSWAR_HERA_LABYRINTH; off = every hook returns false and nothing changes.
/// </summary>
/// <remarks>
/// <para>Entry: gate event.hera_labyrinth (always) -> 513, level 50-200 -> 2001, then map 30 (Athens) / 31 (Sparta)
/// at the GENERATED dock arrival. The client shows its own TS500/TS501 confirm box before it sends the click.</para>
/// <para>Portals: a walked segment passing within the map-portal trigger radius of one of the 36 client portal
/// statics moves the player, on the same map, to the destination isle's GENERATED landing.</para>
/// <para>Zeus' Envoy (006, Quest.xml point): opening it hands out Zeus' Oracle 1 once a realm day, unless an oracle is
/// still carried (IGG: unused oracles are kept). It then offers function 97 home (GENERATED; the maze has no
/// transporter NPC) next to its quest page.</para>
/// <para>Zeus' Envoy N (function 2, NpcFunCredit.lua): choosing the function is one deposit (the script has no
/// button for it). Oracle N -> the client's EXP / TP / 50 Zodiac Energy line for the level band, and Oracle N+1;
/// Envoy 5 also gives Pan's Invitation part 3 while GODSWAR_PAN_ENVOY is on and this week's two map parts are not
/// used up. Once a realm day per envoy. Replies: 2N00+band, 1N00 no oracle, 2N10 already today, 102 bag full.</para>
/// <para>No pop-up follows a consumed oracle: the bag is repainted silently and only received items are announced.</para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleHeraLabyrinthActionAsync(NpcSpawnDefinition npc, int dialogIndex, int subId,
        int[] args, CancellationToken cancellationToken)
    {
        if (!H.IsEnabled)
        {
            return false;
        }

        try
        {
            if (H.TryResolveEntry(npc.NpcKey, dialogIndex, subId, out var mapId))
            {
                await HandleHeraLabyrinthEntryAsync(npc, mapId, cancellationToken);
                return true;
            }

            // Part 2: the capital War Supplier's page-3 "Deposit Zeus' Oracle N" (1010-1014). The page request and
            // the 1000 step stay with WarSupplierDialogue (and the F-53 open-time hand-in).
            if (H.TryResolveWarSupplierDeposit(npc.NpcKey, dialogIndex, ResolveNpcFunctionSelection(subId, args),
                    out var deposited))
            {
                await HandleHeraWarSupplierDepositAsync(npc, deposited, cancellationToken);
                return true;
            }

            if (H.IsZeusEnvoy(npc) && dialogIndex == H.HomeFunction)
            {
                await HandleHeraLabyrinthHomeAsync(npc, cancellationToken);
                return true;
            }

            if (H.TryGetEnvoy(npc, out var envoy) && dialogIndex == H.EnvoyFunction)
            {
                // The page request (choosing the function) resolves to a negative selection, as in the scripted
                // dialogue handler; any button-shaped click is only logged.
                var selection = ResolveNpcFunctionSelection(subId, args);
                if (selection < 0)
                {
                    await HandleHeraEnvoyDepositAsync(npc, envoy, cancellationToken);
                }
                else
                {
                    Console.WriteLine($"[hera] envoy {envoy} unregistered click sub={subId} selection={selection} " +
                        $"args=[{string.Join(',', args)}] character={_character?.Name}");
                }

                return true;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A labyrinth fault must never end the session; an open transaction rolled back.
            Console.Error.WriteLine($"[hera] fault npc={npc.NpcKey} dialog={dialogIndex} sub={subId}: " +
                $"{error.GetType().Name}: {error.Message}");
            return true;
        }

        return false;
    }

    /// <summary>Open: Zeus' Envoy hands out Oracle 1 and offers home; Envoys 1-5 advertise function 2.</summary>
    private async Task<bool> TryHandleHeraLabyrinthNpcOpenAsync(NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (!H.IsHeraNpc(npc) || _character is null || _character.CurrentMap != npc.MapId)
        {
            return false;
        }

        var questFlags = HasQuestFunctionFor(npc.InteractionId) ? QuestContentBaseline.QuestOpenFlags : 0;
        if (H.IsZeusEnvoy(npc))
        {
            try
            {
                await GrantHeraOracleOneAsync(npc, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[hera] oracle 1 fault character={_character.Name}: {error.GetType().Name}: {error.Message}");
            }

            await _session.SendAsync(
                PacketBuilder.NpcFunctionDialogOpenAck(npc.InteractionId, 0x200 | questFlags,
                    PacketBuilder.PackNpcDialogIndices([H.HomeFunction]), npc.NpcKey),
                cancellationToken, "HeraZeusEnvoyOpen");
            return true;
        }

        if (H.TryGetEnvoy(npc, out _))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionDialogOpenAck(npc.InteractionId, 0x200 | questFlags,
                    PacketBuilder.PackNpcDialogIndices([H.EnvoyFunction]), npc.NpcKey),
                cancellationToken, "HeraEnvoyOpen");
            return true;
        }

        return false;
    }

    private bool HeraNear(NpcSpawnDefinition npc) =>
        _character is { } character && character.CurrentMap == npc.MapId &&
        Math.Abs(character.PositionX - npc.X) <= H.InteractionRadius &&
        Math.Abs(character.PositionZ - npc.Z) <= H.InteractionRadius;

    // ---- Entry ------------------------------------------------------------------------------------------------

    private async Task HandleHeraLabyrinthEntryAsync(NpcSpawnDefinition npc, byte mapId, CancellationToken cancellationToken)
    {
        if (!EventDestinationEntryAllowed("hera") || _character!.CurrentMap == mapId)
        {
            return;
        }

        var gmSkips = GateRules.GmMaySkip(GateKeys.HeraLabyrinth) && await HasOperatorAuthorityAsync(cancellationToken);
        if (!gmSkips && !GateSchedules.IsOpen(GateKeys.HeraLabyrinth, _realmCalendar, DateTimeOffset.UtcNow))
        {
            await SendEventDestinationReplyAsync(npc, H.ClosedResult, "hera", $"closed window={H.WindowMode}", cancellationToken);
            return;
        }

        var band = GateRules.Band(GateKeys.HeraLabyrinth, H.MinimumLevel, H.MaximumLevel);
        if (!gmSkips && (_character.Level < band.Minimum || _character.Level > band.Maximum))
        {
            await SendEventDestinationReplyAsync(npc, H.LevelResult, "hera",
                $"level={_character.Level} band={band.Minimum}-{band.Maximum}", cancellationToken);
            return;
        }

        var (x, z) = H.Arrival;
        var outcome = await TryBeginMapTransitionCoreAsync(mapId, x, z, $"hera:{npc.NpcKey}", cancellationToken,
            publishFightStateReset: true);
        Console.WriteLine($"[hera] transport character={_character.Name} level={_character.Level} map={mapId} " +
            $"arrival={x},{z} outcome={outcome}");
    }

    // ---- Portals ----------------------------------------------------------------------------------------------

    private async Task<bool> TryApplyHeraLabyrinthPortalAsync(AcceptedMapMovementSegment movement,
        CancellationToken cancellationToken)
    {
        var length = Math.Sqrt(Math.Pow(movement.End.X - movement.Start.X, 2) + Math.Pow(movement.End.Z - movement.Start.Z, 2));
        if (_character is null || movement.MapId != _character.CurrentMap ||
            !MapTraversalLimits.IsFiniteAndBounded(movement.Start) || !MapTraversalLimits.IsFiniteAndBounded(movement.End) ||
            length < MapTraversalLimits.MinimumAcceptedSegmentLength || length > MapTraversalLimits.MaximumAcceptedSegmentLength ||
            !H.TryResolvePortal(movement.MapId, movement.Start.X, movement.Start.Z, movement.End.X, movement.End.Z,
                MapPortalTriggerRadius, out var from, out var to, out var x, out var z))
        {
            return false;
        }

        var mapId = _character.CurrentMap;
        var outcome = await TryBeginSameMapSceneTransitionAsync(x, z, $"hera-portal:{from}->{to}",
            () => H.IsEnabled && _character?.CurrentMap == mapId, cancellationToken);
        if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation)
        {
            return false;
        }

        Console.WriteLine($"[hera] portal character={_character.Name} map={mapId} isle={from}->{to} landing={x},{z} " +
            $"outcome={outcome}");
        return true;
    }

    // ---- Zeus' Envoy (006) ------------------------------------------------------------------------------------

    private async Task GrantHeraOracleOneAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        if (!HeraNear(npc))
        {
            return;
        }

        var oracleOne = (int)H.OracleItemId(1);
        var granted = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.DailyUsedAsync(H.OracleOneCounter) > 0)
            {
                return false;
            }

            for (var number = 1; number <= H.EnvoyCount; number++)
            {
                if (await tx.CountAsync((int)H.OracleItemId(number)) > 0)
                {
                    return false; // IGG: an unfinished chain keeps its oracle; no second one
                }
            }

            if (!await tx.GrantAsync(oracleOne, 1))
            {
                tx.Rollback();
                return false; // bag full: nothing given, try again
            }

            await tx.DailyAddAsync(H.OracleOneCounter, 1);
            return true;
        }, false, cancellationToken);
        Console.WriteLine($"[hera] oracle 1 character={_character?.Name} granted={granted}");
        if (granted)
        {
            await RefreshAfterEventsNpcAsync([(oracleOne, 1)], cancellationToken);
        }
    }

    private async Task HandleHeraLabyrinthHomeAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        if (_character is null || _character.CurrentMap != npc.MapId || !HeraNear(npc))
        {
            return;
        }

        var sparta = _character.Camp == GameDefaults.SpartaCamp;
        var targetMapId = sparta ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
        var targetX = sparta ? GameDefaults.StartingPositionX : CursedLandTransportProtocol.AthensHome.X;
        var targetZ = sparta ? GameDefaults.StartingPositionZ : CursedLandTransportProtocol.AthensHome.Z;
        var outcome = await TryBeginMapTransitionCoreAsync(targetMapId, targetX, targetZ, $"hera-home:{npc.NpcKey}",
            cancellationToken, publishFightStateReset: true);
        Console.WriteLine($"[hera] home transport character={_character.Name} from={npc.MapId} map={targetMapId} " +
            $"arrival={targetX},{targetZ} outcome={outcome}");
    }

    // ---- Zeus' Envoy 1-5 --------------------------------------------------------------------------------------

    private sealed record HeraDeposit(int Result, int Experience = 0, int TalentPoints = 0, int ZodiacX100 = 0,
        bool PartThree = false, List<(int ItemId, int Quantity)>? Granted = null);

    /// <summary>The client result numbers one deposit surface draws (the maze envoy page or the supplier's page 3).</summary>
    private sealed record HeraDepositCodes(int Function, int Missing, int AlreadyDone, int BagFull, string Label);

    private Task HandleHeraEnvoyDepositAsync(NpcSpawnDefinition npc, int envoy, CancellationToken cancellationToken) =>
        HandleHeraDepositAsync(npc, envoy, H.Band(_character?.Level ?? H.MinimumLevel),
            new HeraDepositCodes(H.EnvoyFunction, H.MissingOracleResult(envoy), H.AlreadyDoneResult(envoy),
                H.BagFullResult, "HeraEnvoy"), cancellationToken);

    /// <summary>
    /// Part 2: the capital War Supplier (NpcFunMaterialBack.lua page 3). [client] NF_L0_SY1001 "identical to that in the
    /// maze": the same oracle in, the same next oracle (and part 3 at Oracle 5) out, the same reward lines and the
    /// same once-a-day counters as Envoy N, so an oracle is never cashed twice a day. GENERATED: Lv150+ is paid band 9
    /// (the only lines this page draws), and a bag-full refusal ends with 3001 plus a chat line.
    /// </summary>
    private Task HandleHeraWarSupplierDepositAsync(NpcSpawnDefinition npc, int oracle, CancellationToken cancellationToken) =>
        HandleHeraDepositAsync(npc, oracle, H.WarSupplierBand(_character?.Level ?? H.MinimumLevel),
            new HeraDepositCodes(H.WarSupplierFunction, H.WarSupplierMissingResult(oracle), H.AlreadyDoneResult(oracle),
                H.WarSupplierEndResult, "HeraSupplier"), cancellationToken);

    private async Task HandleHeraDepositAsync(NpcSpawnDefinition npc, int envoy, int band, HeraDepositCodes codes,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character || !HeraNear(npc))
        {
            return;
        }

        var level = character.Level;
        var (experience, talent) = H.RewardForBand(envoy, band);
        var week = PanInvitationProtocol.WeekKey(EventsNpcDay(EventsNpcNow));
        var bagFull = false;
        var deposit = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.DailyUsedAsync(H.DailyCounter(envoy)) > 0)
            {
                return new HeraDeposit(codes.AlreadyDone);
            }

            if (!await tx.ConsumeAsync((int)H.OracleItemId(envoy), 1))
            {
                return new HeraDeposit(codes.Missing);
            }

            var granted = new List<(int ItemId, int Quantity)>();
            if (envoy < H.EnvoyCount)
            {
                var next = (int)H.OracleItemId(envoy + 1);
                if (!await tx.GrantAsync(next, 1))
                {
                    tx.Rollback();
                    bagFull = true;
                    return new HeraDeposit(codes.BagFull);
                }

                granted.Add((next, 1));
            }

            var partThree = false;
            if (envoy == H.EnvoyCount && PanInvitationProtocol.EnvoyEnabled &&
                await PanMapPartsThisWeekAsync(tx, week) < PanInvitationProtocol.MapPartsPerWeek)
            {
                if (!await tx.GrantAsync((int)PanInvitationProtocol.PartThree, 1))
                {
                    tx.Rollback();
                    bagFull = true;
                    return new HeraDeposit(codes.BagFull);
                }

                await PanAddMapPartAsync(tx, week);
                granted.Add(((int)PanInvitationProtocol.PartThree, 1));
                partThree = true;
            }

            await tx.AddTalentPointsAsync(talent);
            var zodiac = await tx.AddZodiacEnergyAsync(H.RewardZodiacEnergy); // 0 when the store is full
            await tx.DailyAddAsync(H.DailyCounter(envoy), 1);
            return new HeraDeposit(H.RewardResult(envoy, band), experience, talent, zodiac, partThree, granted);
        }, new HeraDeposit(codes.Missing), cancellationToken);

        Console.WriteLine($"[hera] {codes.Label} oracle={envoy} npc={npc.NpcKey} character={character.Name} level={level} " +
            $"band={band} result={deposit.Result} exp={deposit.Experience} tp={deposit.TalentPoints} " +
            $"zodiacX100={deposit.ZodiacX100} part3={deposit.PartThree}");
        if (deposit.Granted is null)
        {
            if (bagFull && codes.Function == H.WarSupplierFunction)
            {
                // Page 3 has no bag-full line; nothing was taken (the transaction rolled back).
                await SendEventsNpcNoticeAsync("Your bag is full! Make room before depositing Zeus' Oracle.",
                    "HeraSupplierBagFull", cancellationToken);
            }

            await SendEventsNpcReplyAsync(npc, codes.Function, [deposit.Result], $"{codes.Label}Refused", cancellationToken);
            return;
        }

        // Committed: repaint the bag (the oracle disappears silently), then pay what lives outside the transaction.
        await RefreshAfterEventsNpcAsync(deposit.Granted, cancellationToken);
        await _session.SendAsync(PacketBuilder.TalentPointGain(deposit.TalentPoints), cancellationToken, $"{codes.Label}Talent");
        if (deposit.ZodiacX100 > 0)
        {
            await _session.SendAsync(PacketBuilder.ZodiacEnergyIncrease(_character!.ZodiacEnergy, deposit.ZodiacX100),
                cancellationToken, $"{codes.Label}Zodiac");
            await SendZodiacFullSyncAsync(cancellationToken);
        }

        // The client prints the exact amount, so it is paid as is (not multiplied by exp.events).
        await PayEventsNpcExperienceAsync(deposit.Experience, codes.Label, cancellationToken, eventRate: false);
        await SendEventsNpcReplyAsync(npc, codes.Function, [deposit.Result], $"{codes.Label}Paid", cancellationToken);
    }

    // ---- Login rescue -----------------------------------------------------------------------------------------

    /// <summary>A character saved on map 30 / 31 while the switch is off logs in at the capital.</summary>
    private bool TryRecoverHeraLabyrinthLogin()
    {
        if (_character is null || !H.IsLabyrinthMap(_character.CurrentMap) || H.IsEnabled)
        {
            return false;
        }

        var saved = _character.CurrentMap;
        GameDefaults.InitializeStartingLocation(_character);
        _unavailableInstanceReconnectMap = saved;
        _positionDirty = true;
        Console.WriteLine($"[hera] switch off: saved map {saved} recovered to capital character={_character.Name} " +
            $"map={_character.CurrentMap}");
        return true;
    }
}
