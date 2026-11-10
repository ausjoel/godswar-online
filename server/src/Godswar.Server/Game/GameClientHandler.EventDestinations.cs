using Godswar.Server.Application.Gates;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.State;
using E = Godswar.Server.Domain.World.Content.EventDestinationsProtocol;

namespace Godswar.Server.Game;

/// <summary>
/// PAN CHAIN (W3, the admin F-50 / F-49): the Event Transporter's Treasure Land (202 -> map 25) and Sicily (700 -> map 32)
/// buttons, the two maps' rosters and their home transporters. GODSWAR_TREASURE_LAND / GODSWAR_SICILY; with a switch
/// off every hook returns false and the old answers stand (202 -> 1010, 700 -> 2702).
/// </summary>
/// <remarks>
/// Treasure Land: window (gate event.treasure_land, Sun 22:00-23:00) -> 1010, level 60-200 -> 2001, then the move and
/// 40 Wooden Tools (10080, IGG). The tools' net-capture mechanic is NOT built.
/// Sicily: level 25-200 -> 2001; five visits a realm day -> 2701; Friday 22:00-24:00 (gate event.sicily) free; any
/// other day one Sicilian Spell (3819) is consumed, none -> 2702; on a Friday before 22:00 -> 2700. The fifth visit
/// of the day also grants Pan's Invitation part 4 (IGG acid=164) if this week's two map parts are not used up; a full
/// bag refuses the visit with 2727 (the Event Transporter's own line). A refused map transfer hands everything back.
/// </remarks>
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleEventDestinationActionAsync(NpcSpawnDefinition npc, int dialogIndex, int subId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (E.IsTreasureLandClick(npc.NpcKey, dialogIndex, subId))
            {
                await HandleTreasureLandEntryAsync(npc, cancellationToken);
                return true;
            }

            if (E.IsSicilyClick(npc.NpcKey, dialogIndex, subId))
            {
                await HandleSicilyEntryAsync(npc, cancellationToken);
                return true;
            }

            if (E.IsHomeTransporter(npc) && dialogIndex == E.HomeFunction && subId == -1)
            {
                await HandleEventDestinationHomeAsync(npc, cancellationToken);
                return true;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A destination fault must never end the session; an open transaction rolled back.
            Console.Error.WriteLine($"[event-destination] fault npc={npc.NpcKey} dialog={dialogIndex} sub={subId}: " +
                $"{error.GetType().Name}: {error.Message}");
            return true;
        }

        return false;
    }

    /// <summary>Open: the home transporters advertise function 97; the other plain actors show their description.</summary>
    private async Task<bool> TryHandleEventDestinationNpcOpenAsync(NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (E.IsHomeTransporter(npc))
        {
            await _session.SendAsync(PacketBuilder.NpcDialogOpenAck(npc.InteractionId, [E.HomeFunction], npc.NpcKey),
                cancellationToken, "EventDestinationHomeOpen");
            return true;
        }

        var injected = npc.MapId == E.TreasureLandMapId && E.TreasureLandEnabled && npc.NpcKey.StartsWith("Agate_", StringComparison.Ordinal) ||
            npc.MapId == E.SicilyMapId && E.SicilyEnabled && npc.NpcKey.StartsWith("Sicily_", StringComparison.Ordinal);
        if (!injected || ResolveScriptedNpcDialogues(npc) is not null)
        {
            return false;
        }

        await SendNpcDescriptionOpenAsync(npc, cancellationToken);
        return true;
    }

    private bool EventDestinationEntryAllowed(string tag)
    {
        if (_character is null || _account is null || _pendingMapTransition is not null || !_registered ||
            !_worldPresenceAnnounced)
        {
            Console.WriteLine($"[{tag}] entry ignored character={_character?.Name} pending={_pendingMapTransition is not null}");
            return false;
        }

        return true;
    }

    private async Task SendEventDestinationReplyAsync(NpcSpawnDefinition npc, int result, string tag, string reason,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(npc.InteractionId, CursedLandTransportProtocol.EventTransporterDialogIndex, result),
            cancellationToken, "EventDestinationRefused");
        Console.WriteLine($"[{tag}] refused character={_character?.Name} result={result} {reason}");
    }

    // ---- Treasure Land -------------------------------------------------------------------------------------

    private async Task HandleTreasureLandEntryAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        if (!EventDestinationEntryAllowed("treasure-land") || _character!.CurrentMap == E.TreasureLandMapId)
        {
            return;
        }

        var gmSkips = GateRules.GmMaySkip(GateKeys.TreasureLand) && await HasOperatorAuthorityAsync(cancellationToken);
        if (!gmSkips && !GateSchedules.IsOpen(GateKeys.TreasureLand, _realmCalendar, DateTimeOffset.UtcNow))
        {
            await SendEventDestinationReplyAsync(npc, E.TreasureLandClosedResult, "treasure-land", "closed", cancellationToken);
            return;
        }

        var band = GateRules.Band(GateKeys.TreasureLand, E.TreasureLandMinimumLevel, E.TreasureLandMaximumLevel);
        if (!gmSkips && (_character.Level < band.Minimum || _character.Level > band.Maximum))
        {
            await SendEventDestinationReplyAsync(npc, E.LevelResult, "treasure-land",
                $"level={_character.Level} band={band.Minimum}-{band.Maximum}", cancellationToken);
            return;
        }

        // IGG: "40 Wooden Tools by the Event Transporter right after stepping onto the Treasure Land". Granted (and the
        // bag repainted) before the move, like Pan's letter, and taken back if the move is refused. A bag without room
        // simply gets none. Stack 40 <= 99.
        var tools = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.GrantAsync((int)E.WoodenToolItemId, E.WoodenToolsOnArrival, bound: true))
            {
                return true;
            }

            tx.Rollback();
            return false;
        }, false, cancellationToken);
        if (tools)
        {
            await RefreshAfterEventsNpcAsync([((int)E.WoodenToolItemId, E.WoodenToolsOnArrival)], cancellationToken);
        }

        var (x, z) = E.TreasureLandArrival(_character.Camp);
        var outcome = await TryBeginMapTransitionCoreAsync(E.TreasureLandMapId, x, z, $"treasure-land:{npc.NpcKey}",
            cancellationToken, publishFightStateReset: true);
        Console.WriteLine($"[treasure-land] transport character={_character.Name} level={_character.Level} " +
            $"arrival={x},{z} tools={tools} outcome={outcome}");
        if (tools && outcome is not (SceneTransitionOutcome.CommittedAwaitingReadiness or
                SceneTransitionOutcome.CommittedRequiresReconnect))
        {
            await RunEventsNpcAsync(tx => tx.ConsumeAsync((int)E.WoodenToolItemId, E.WoodenToolsOnArrival), false,
                cancellationToken);
            await RefreshAfterEventsNpcAsync([], cancellationToken);
            Console.Error.WriteLine($"[treasure-land] transfer refused, wooden tools taken back character={_character.Name}");
        }
    }

    // ---- Sicily --------------------------------------------------------------------------------------------

    private enum SicilyTicket { Free, Spell }

    private async Task HandleSicilyEntryAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        if (!EventDestinationEntryAllowed("sicily") || _character!.CurrentMap == E.SicilyMapId)
        {
            return;
        }

        var realm = _realmCalendar.ToRealmTime(DateTimeOffset.UtcNow).DateTime;
        var gmSkips = GateRules.GmMaySkip(GateKeys.Sicily) && await HasOperatorAuthorityAsync(cancellationToken);
        var band = GateRules.Band(GateKeys.Sicily, E.SicilyMinimumLevel, E.SicilyMaximumLevel);
        if (!gmSkips && (_character.Level < band.Minimum || _character.Level > band.Maximum))
        {
            await SendEventDestinationReplyAsync(npc, E.LevelResult, "sicily",
                $"level={_character.Level} band={band.Minimum}-{band.Maximum}", cancellationToken);
            return;
        }

        var free = gmSkips || GateSchedules.IsOpen(GateKeys.Sicily, _realmCalendar, DateTimeOffset.UtcNow);
        if (!free && E.SicilySpellForbiddenAt(realm))
        {
            await SendEventDestinationReplyAsync(npc, E.SicilyNotYetResult, "sicily", $"friday before 22:00 realm={realm:ddd HH:mm}",
                cancellationToken);
            return;
        }

        var ticket = free ? SicilyTicket.Free : SicilyTicket.Spell;
        var week = PanInvitationProtocol.WeekKey(EventsNpcDay(EventsNpcNow));
        var (result, partFour) = await RunEventsNpcAsync(async tx =>
        {
            var visits = await tx.DailyUsedAsync("sicily_visits");
            if (visits >= E.SicilyVisitsPerDay)
            {
                return (E.SicilyTryAnotherDayResult, false);
            }

            if (ticket == SicilyTicket.Spell && !await tx.ConsumeAsync((int)E.SicilianSpellItemId, 1))
            {
                return (E.SicilyNoSpellResult, false);
            }

            var givePart = visits + 1 == E.SicilyVisitsPerDay && PanInvitationProtocol.EnvoyEnabled &&
                await PanMapPartsThisWeekAsync(tx, week) < PanInvitationProtocol.MapPartsPerWeek;
            if (givePart)
            {
                if (!await tx.GrantAsync((int)PanInvitationProtocol.PartFour, 1))
                {
                    tx.Rollback();
                    return (E.SicilyPartFourBagFullResult, false);
                }

                await PanAddMapPartAsync(tx, week);
            }

            await tx.DailyAddAsync("sicily_visits", 1);
            return (0, givePart);
        }, (E.SicilyNoSpellResult, false), cancellationToken);
        if (result != 0)
        {
            await SendEventDestinationReplyAsync(npc, result, "sicily", $"ticket={ticket}", cancellationToken);
            return;
        }

        // The spell is gone from the bag now: repaint it before the move (silently; only part 4 is announced).
        await RefreshAfterEventsNpcAsync(partFour ? [((int)PanInvitationProtocol.PartFour, 1)] : [], cancellationToken);
        var (x, z) = E.SicilyArrival(_character.Camp);
        var outcome = await TryBeginMapTransitionCoreAsync(E.SicilyMapId, x, z, $"sicily:{npc.NpcKey}",
            cancellationToken, publishFightStateReset: true);
        Console.WriteLine($"[sicily] transport character={_character.Name} level={_character.Level} ticket={ticket} " +
            $"part4={partFour} arrival={x},{z} outcome={outcome}");
        if (outcome is SceneTransitionOutcome.CommittedAwaitingReadiness or SceneTransitionOutcome.CommittedRequiresReconnect)
        {
            return;
        }

        // The move was refused after the visit was paid: hand the spell, the visit and the part back.
        await RunEventsNpcAsync(async tx =>
        {
            if (ticket == SicilyTicket.Spell)
            {
                await tx.GrantAsync((int)E.SicilianSpellItemId, 1, bound: null);
            }

            if (partFour)
            {
                await tx.ConsumeAsync((int)PanInvitationProtocol.PartFour, 1);
                await tx.ScalarAsync(
                    "UPDATE public.finalise2_npc_daily SET used = GREATEST(used - 1, 0) WHERE character_id = @c AND day = @w AND counter = 'pan_map_parts';",
                    ("w", week));
            }

            await tx.DailyAddAsync("sicily_visits", -1);
            return true;
        }, false, cancellationToken);
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        Console.Error.WriteLine($"[sicily] transfer refused, visit handed back character={_character.Name}");
    }

    // ---- Home transporters on maps 25 / 32 ------------------------------------------------------------------

    private async Task HandleEventDestinationHomeAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        if (_character is null || _character.CurrentMap != npc.MapId)
        {
            return;
        }

        // The captured Cursed Land home landing (Athens 20,-100) and the reviewed Sparta capital point.
        var sparta = _character.Camp == GameDefaults.SpartaCamp;
        var targetMapId = sparta ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
        var targetX = sparta ? GameDefaults.StartingPositionX : CursedLandTransportProtocol.AthensHome.X;
        var targetZ = sparta ? GameDefaults.StartingPositionZ : CursedLandTransportProtocol.AthensHome.Z;
        var outcome = await TryBeginMapTransitionCoreAsync(targetMapId, targetX, targetZ,
            $"event-destination-home:{npc.NpcKey}", cancellationToken, publishFightStateReset: true);
        Console.WriteLine($"[event-destination] home transport character={_character.Name} from={npc.MapId} " +
            $"map={targetMapId} arrival={targetX},{targetZ} outcome={outcome}");
    }

    /// <summary>
    /// A character saved on map 25 / 32 while that destination's switch is off logs in at the capital, so switching
    /// a destination off never strands anyone on a map without its transporters.
    /// </summary>
    private bool TryRecoverEventDestinationLogin()
    {
        if (_character is null ||
            !(_character.CurrentMap == E.TreasureLandMapId && !E.TreasureLandEnabled ||
              _character.CurrentMap == E.SicilyMapId && !E.SicilyEnabled))
        {
            return false;
        }

        var saved = _character.CurrentMap;
        GameDefaults.InitializeStartingLocation(_character);
        _unavailableInstanceReconnectMap = saved;
        _positionDirty = true;
        Console.WriteLine($"[event-destination] switch off: saved map {saved} recovered to capital " +
            $"character={_character.Name} map={_character.CurrentMap}");
        return true;
    }
}
