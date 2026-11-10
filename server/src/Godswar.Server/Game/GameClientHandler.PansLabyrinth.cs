using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// Pan's Labyrinth (map 40). GATED OFF unless <c>GODSWAR_PAN_LABYRINTH=1</c>; with the
/// switch off every hook below returns without touching the session.
/// </summary>
/// <remarks>
/// <para>
/// Entry is the capital Event Transporter's page-one button 218, the same NPC and
/// window as the captured Cursed Land entry (<see cref="HandleCursedLandTwoTeleportAsync"/>).
/// Refusals use only lines the client script draws: 2705 (closed), 2706 (no letter)
/// and 2001 (level). The letter is consumed with the entry row in one transaction,
/// and handed back if the map transfer is then refused.
/// </para>
/// <para>
/// On the map: the Main City Transporter (011) reuses the captured Cursed Land home
/// shape (function 97). A servant (001-009) pays its GENERATED reward when its window
/// is opened and then shows its own description; how the original server showed the
/// <c>Pans_Labyrinth_a/b/c</c> texts is uncaptured, so they are not sent. The Avatar of
/// Pan (010) only shows its description: its certificate exchange
/// (<c>NpcFunPan.lua</c>) has no captured click.
/// </para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandlePansLabyrinthActionAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        if (PansLabyrinthProtocol.IsEntryClick(npc.NpcKey, dialogIndex, subId))
        {
            await HandlePansLabyrinthEntryAsync(npc, cancellationToken);
            return true;
        }

        if (!PansLabyrinthProtocol.IsEnabled || !PansLabyrinthProtocol.IsPanNpc(npc))
        {
            return false;
        }

        if (PansLabyrinthProtocol.IsHomeTransporter(npc) &&
            dialogIndex == PansLabyrinthProtocol.HomeTeleportFunction &&
            subId == -1)
        {
            await HandlePansLabyrinthHomeAsync(npc, cancellationToken);
            return true;
        }

        // PAN CHAIN: the Avatar's scripted functions (necklace 41, draw 10) go on to the scripted dialogues.
        if (PansLabyrinthProtocol.IsAvatar(npc) && ResolveScriptedNpcDialogues(npc) is { } avatarDialogues &&
            avatarDialogues.ContainsKey(dialogIndex))
        {
            return false;
        }

        Console.WriteLine(
            $"[pan] unregistered action npc={npc.InteractionId} key={npc.NpcKey} " +
            $"dialog={dialogIndex} subId={subId}");
        return true;
    }

    private async Task<bool> TryHandlePansLabyrinthNpcOpenAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (!PansLabyrinthProtocol.IsEnabled || !PansLabyrinthProtocol.IsPanNpc(npc))
        {
            return false;
        }

        if (PansLabyrinthProtocol.IsHomeTransporter(npc))
        {
            await _session.SendAsync(
                PacketBuilder.NpcDialogOpenAck(
                    npc.InteractionId,
                    [PansLabyrinthProtocol.HomeTeleportFunction],
                    npc.NpcKey),
                cancellationToken,
                "PansLabyrinthHomeOpen");
            Console.WriteLine($"[pan] home transporter open npc={npc.InteractionId}");
            return true;
        }

        // PAN CHAIN: an Avatar with scripted functions (marriage necklace, certificate draw) opens their menu
        // instead of the bare description; before this fix the necklace (F-130/F-78) was unreachable.
        if (PansLabyrinthProtocol.IsAvatar(npc) && ResolveScriptedNpcDialogues(npc) is not null)
        {
            return false;
        }

        if (PansLabyrinthProtocol.TryGetServant(npc, out var servant))
        {
            await ClaimPansLabyrinthServantAsync(npc, servant, cancellationToken);
        }

        await SendNpcDescriptionOpenAsync(npc, cancellationToken);
        return true;
    }

    private async Task HandlePansLabyrinthEntryAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }

        if (_character.CurrentMap == PansLabyrinthProtocol.MapId ||
            _pendingMapTransition is not null ||
            !_registered ||
            !_worldPresenceAnnounced)
        {
            Console.WriteLine(
                $"[pan] entry ignored character={_character.Name} map={_character.CurrentMap} " +
                $"pending={_pendingMapTransition is not null}");
            return;
        }

        var realmNow = _realmCalendar.ToRealmTime(DateTimeOffset.UtcNow).DateTime;
        // FINALISE2: Gates table (band, opening mode, GM rule) for Pan's Labyrinth.
        var gateKey = Godswar.Server.Application.Gates.GateKeys.PansLabyrinth;
        var band = Godswar.Server.Application.Gates.GateRules.Band(gateKey, PansLabyrinthProtocol.MinimumLevel, PansLabyrinthProtocol.MaximumLevel);
        var gmSkips = Godswar.Server.Application.Gates.GateRules.GmMaySkip(gateKey) && await HasOperatorAuthorityAsync(cancellationToken);
        if (!gmSkips && !Godswar.Server.Application.Gates.GateSchedules.IsOpen(gateKey, _realmCalendar, DateTimeOffset.UtcNow))
        {
            await SendPansLabyrinthRefusalAsync(npc, PansLabyrinthProtocol.ClosedResultSubId,
                $"closed window={PansLabyrinthProtocol.WindowMode} realm={realmNow:ddd HH:mm}", cancellationToken);
            return;
        }

        if (!gmSkips && (_character.Level < band.Minimum || _character.Level > band.Maximum)) // FINALISE2
        {
            await SendPansLabyrinthRefusalAsync(npc, PansLabyrinthProtocol.LevelResultSubId,
                $"level={_character.Level} band={band.Minimum}-" + // FINALISE2
                $"{band.Maximum}", cancellationToken);
            return;
        }

        var slot = FindPansLabyrinthLetterSlot();
        if (slot < 0)
        {
            await SendPansLabyrinthRefusalAsync(npc, PansLabyrinthProtocol.NoLetterResultSubId,
                "no letter", cancellationToken);
            return;
        }

        var letter = KitBagSlots.GetItem(_character.KitBag, slot);
        var entry = await _store.OpenPansLabyrinthEntryAsync(_account.Id, _character.Id, slot,
            PansLabyrinthProtocol.InvitationLetterItemId, letter.Stack, cancellationToken);
        if (entry.Status == PansLabyrinthEntryStatus.Unavailable)
        {
            Console.Error.WriteLine(
                "[pan] pan_labyrinth tables are absent: apply Development/pan-labyrinth/pan-labyrinth.sql");
            await SendPansLabyrinthRefusalAsync(npc, PansLabyrinthProtocol.ClosedResultSubId,
                "unavailable", cancellationToken);
            return;
        }

        if (entry.Status != PansLabyrinthEntryStatus.Opened)
        {
            await SendPansLabyrinthRefusalAsync(npc, PansLabyrinthProtocol.NoLetterResultSubId,
                $"letter changed slot={slot}", cancellationToken);
            return;
        }

        _character.KitBag = ConsumeQuestBagProjection(_character.KitBag, slot);
        await SendKitBagRefreshAsync(cancellationToken);

        var (x, z) = PansLabyrinthProtocol.Arrival;
        var outcome = await TryBeginMapTransitionCoreAsync(
            PansLabyrinthProtocol.MapId,
            x,
            z,
            $"pans-labyrinth:{npc.NpcKey}",
            cancellationToken,
            publishFightStateReset: true);
        if (outcome == SceneTransitionOutcome.CommittedRequiresReconnect)
        {
            return;
        }

        if (outcome != SceneTransitionOutcome.CommittedAwaitingReadiness)
        {
            await RefundPansLabyrinthLetterAsync(entry.EntryId, letter.Bound, cancellationToken);
            return;
        }

        Console.WriteLine(
            $"[pan] admitted character={_character.Name} level={_character.Level} entry={entry.EntryId} " +
            $"npc={npc.InteractionId} map={PansLabyrinthProtocol.MapId} arrival={x},{z}");
    }

    private int FindPansLabyrinthLetterSlot()
    {
        if (_character is null)
        {
            return -1;
        }

        for (var slot = 0; slot < 96; slot++)
        {
            var item = KitBagSlots.GetItem(_character.KitBag, slot);
            if (item.Id == PansLabyrinthProtocol.InvitationLetterItemId && item.Stack > 0)
            {
                return slot;
            }
        }

        return -1;
    }

    private async Task RefundPansLabyrinthLetterAsync(
        long entryId,
        short bound,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        try
        {
            var result = await RequireOperatorStore().GrantItemAsync(
                _character.Id,
                PansLabyrinthProtocol.InvitationLetterItemId,
                1,
                1,
                bound,
                CancellationToken.None);
            await _store.MarkPansLabyrinthEntryRefundedAsync(entryId, CancellationToken.None);
            var refresh = await ReloadAfterOperatorChangeAsync("pan-refund", cancellationToken);
            Console.Error.WriteLine(
                $"[pan] transfer refused, letter handed back character={_character.Name} entry={entryId} " +
                $"grant={result.Outcome} {result.Detail} refresh={refresh}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[pan] transfer refused and the letter refund FAILED character={_character.Name} " +
                $"entry={entryId}: {error.Message}");
        }
    }

    private async Task SendPansLabyrinthRefusalAsync(
        NpcSpawnDefinition npc,
        int resultSubId,
        string reason,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npc.InteractionId,
                PansLabyrinthProtocol.DialogIndex,
                resultSubId),
            cancellationToken,
            "PansLabyrinthRefused");
        Console.WriteLine(
            $"[pan] entry refused character={_character?.Name} result={resultSubId} {reason}");
    }

    private async Task ClaimPansLabyrinthServantAsync(
        NpcSpawnDefinition npc,
        int servant,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character ||
            character.CurrentMap != PansLabyrinthProtocol.MapId)
        {
            return;
        }

        var dx = (double)character.PositionX - npc.X;
        var dz = (double)character.PositionZ - npc.Z;
        var radius = (double)PansLabyrinthProtocol.InteractionRadius;
        if (!double.IsFinite(dx) || !double.IsFinite(dz) || dx * dx + dz * dz > radius * radius)
        {
            Console.WriteLine(
                $"[pan] servant {servant} too far character={character.Name} " +
                $"at={character.PositionX:F1},{character.PositionZ:F1}");
            return;
        }

        var claim = await _store.ClaimPansLabyrinthServantAsync(character.Id, servant, cancellationToken);
        if (claim.Status != PansLabyrinthClaimStatus.Claimed)
        {
            Console.WriteLine(
                $"[pan] servant {servant} no reward character={character.Name} status={claim.Status}");
            return;
        }

        var (experience, talentPoints) = PansLabyrinthProtocol.ServantReward(character.Level, claim.Ordinal);
        try
        {
            await PayZeusGiftProgressionAsync(
                experience,
                checked(talentPoints * Godswar.Server.Application.ZeusGift.ZeusGiftPolicy.TalentExperiencePerPoint),
                "pan-servant",
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await _store.UnclaimPansLabyrinthServantAsync(character.Id, servant, CancellationToken.None);
            Console.Error.WriteLine(
                $"[pan] servant {servant} payment failed character={character.Name}: {error.Message}");
            return;
        }

        Console.WriteLine(
            $"[pan] servant {servant} reward character={character.Name} ordinal={claim.Ordinal} " +
            $"exp={experience} talent-points={talentPoints} (generated)");
    }

    private async Task HandlePansLabyrinthHomeAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (_character is null || _character.CurrentMap != PansLabyrinthProtocol.MapId)
        {
            return;
        }

        // The captured Cursed Land home landing (Athens 20,-100) and the reviewed
        // Sparta capital point, as HandleCursedLandHomeAsync uses them.
        var sparta = _character.Camp == GameDefaults.SpartaCamp;
        var targetMapId = sparta ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
        var targetX = sparta ? GameDefaults.StartingPositionX : CursedLandTransportProtocol.AthensHome.X;
        var targetZ = sparta ? GameDefaults.StartingPositionZ : CursedLandTransportProtocol.AthensHome.Z;
        var outcome = await TryBeginMapTransitionCoreAsync(
            targetMapId,
            targetX,
            targetZ,
            $"pans-labyrinth-home:{npc.NpcKey}",
            cancellationToken,
            publishFightStateReset: true);
        Console.WriteLine(
            $"[pan] home transport character={_character.Name} camp={_character.Camp} " +
            $"map={targetMapId} arrival={targetX},{targetZ} outcome={outcome}");
    }

    /// <summary>
    /// A character saved on map 40 while the feature is off logs in at the capital,
    /// so switching the feature off never strands anyone on an empty map.
    /// </summary>
    private bool TryRecoverPansLabyrinthLogin()
    {
        if (_character is null ||
            _character.CurrentMap != PansLabyrinthProtocol.MapId ||
            PansLabyrinthProtocol.IsEnabled)
        {
            return false;
        }

        GameDefaults.InitializeStartingLocation(_character);
        _unavailableInstanceReconnectMap = PansLabyrinthProtocol.MapId;
        _positionDirty = true;
        Console.WriteLine(
            $"[pan] feature off: saved map 40 recovered to capital character={_character.Name} " +
            $"map={_character.CurrentMap}");
        return true;
    }
}
