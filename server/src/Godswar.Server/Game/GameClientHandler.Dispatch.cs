using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Talents;
using Godswar.Server.Application.World;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Per-tick traffic that would bury the diagnostic file: the UI heartbeat,
    /// the client's movement stream and the world spawn/vitals updates.
    /// </summary>
    private static bool IsTraceNoise(ushort opcode) =>
        opcode is 10015 or 10016 or 10017 or 10194;

    private async Task HandlePacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_session.AllowsPayloadDiagnostics)
        {
            LogReceived(packet);
        }

        // A diagnostic for clicks that produce no answer at all: the file trace
        // survives the container's folded stdout, so the next click can be read
        // back with its opcode and payload even when the handler never runs.
        // Per-tick traffic is filtered out - thousands of 20-byte movement and
        // world-update frames otherwise bury the clicks - and every other frame
        // keeps a bounded preview instead of the old 64-byte cap, which hid the
        // 92-byte bag activation (opcode 10051) that item use travels on.
        if (!IsTraceNoise(packet.Opcode))
        {
            var payload = packet.Payload;
            var shown = Math.Min(payload.Length, 24);
            QuestFrameTrace.Append(
                $"[recv] op={packet.Opcode} len={packet.Length} " +
                $"payload={Convert.ToHexString(payload[..shown])}",
                []);
        }

        if (_session.BoundGamePrincipal is not null &&
            _account is null &&
            packet.Opcode is not (
                Opcodes.LoginGameServer or
                Opcodes.Ping or
                Opcodes.UiHeartbeat))
        {
            _session.Disconnect();
            return;
        }

        if (!AuthorizeAuthenticatedPacket())
        {
            return;
        }

        if (IsMapTransitionPending &&
            !IsAllowedDuringMapTransition(packet.Opcode))
        {
            return;
        }

        if (_goldBoxDial is not null) await SettleGoldBoxDialAsync(packet.Opcode, cancellationToken);
        if (await TryHandlePartyPacketAsync(packet, cancellationToken)) return;
        if (await TryHandleRelationPacketAsync(packet, cancellationToken)) return;
        if (await TryHandleMentorPacketAsync(packet, cancellationToken)) return; // ECONOMY mentor (off unless GODSWAR_MENTOR=1)
        if (await TryHandlePlayerTradePacketAsync(packet, cancellationToken)) return; // TRADE
        if (await TryHandlePetCapturePacketAsync(packet, cancellationToken)) return;
        if (await TryHandleTimePrizePacketAsync(packet, cancellationToken)) return; // W1 prize claims
        if (await TryHandleProfessionPacketAsync(packet, cancellationToken)) return; // PROF slice 1 (10340)
        if (await TryHandleRookieGrowthPacketAsync(packet, cancellationToken)) return; // F-138 newbie growth rewards
        if (await TryHandleGatheringPacketAsync(packet, cancellationToken)) return; // PROF slice 2 (10342/10344)

        switch (packet.Opcode)
        {
            case PacketBuilder.GoldBoxRequestOpcode:
                await HandleGoldBoxAsync(packet, cancellationToken);
                break;
            case Opcodes.DesignationSelection:
                await HandleTitleSelectionAsync(packet, cancellationToken);
                break;
            case Opcodes.LoginGameServer:
                await HandleGameLoginAsync(packet, cancellationToken);
                break;
            case Opcodes.RoleInfo:
                await SendCharacterPreviewAsync(cancellationToken);
                break;
            case Opcodes.CreateRole:
                await HandleCreateRoleAsync(packet, cancellationToken);
                break;
            case Opcodes.DeleteRole:
                await HandleDeleteRoleAsync(packet, cancellationToken);
                break;
            case Opcodes.EnterGame:
                await HandleEnterGameAsync(cancellationToken);
                break;
            case Opcodes.Ping:
                await _session.SendAsync(packet.Buffer, cancellationToken, "PingEcho");
                break;
            case Opcodes.UiHeartbeat:
                await _session.SendAsync(packet.Buffer, cancellationToken, "UiHeartbeatEcho");
                break;
            case Opcodes.Talk:
                // A recognised developer command is consumed locally and never
                // reaches a chat channel; everything else is chat, which routes by
                // the channel the client's own frame carries.
                if (await TryHandleAfkRevivalCommandAsync(packet, cancellationToken)) break; // ARENA
                if (!await HandleDeveloperItemCommandAsync(packet, cancellationToken))
                {
                    await HandleChatAsync(packet, cancellationToken);
                }

                break;
            case Opcodes.WalkBegin:
            case Opcodes.WalkEnd:
            case Opcodes.Walk:
                if (RejectDeadLegacyMovement(packet))
                {
                    break;
                }
                if (IsMapTransitionPending)
                {
                    break;
                }
                if (packet.Opcode != Opcodes.Walk &&
                    RejectBlockedNonWalkMovement())
                {
                    break;
                }
                if (packet.Opcode == Opcodes.WalkBegin)
                {
                    await InterruptPendingSkillCastAsync(
                        SkillCastInterruptionReason.Movement,
                        cancellationToken);
                }
                if (packet.Opcode == Opcodes.Walk)
                {
                    if (!await HandleWalkAsync(packet, cancellationToken))
                    {
                        break;
                    }
                }
                else if (packet.Opcode == Opcodes.WalkEnd)
                {
                    await PersistCharacterPositionAsync(force: true, cancellationToken);
                }

                await BroadcastToCurrentMapAsync(packet, cancellationToken);
                break;
            case Opcodes.SkillCast:
                await HandleSkillCastAsync(packet, cancellationToken);
                break;
            case Opcodes.SkillCastInterrupt:
                await HandleSkillCastInterruptRequestAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.PlayerStateAction:
                await HandlePlayerStateActionAsync(packet, cancellationToken);
                break;
            case Opcodes.BasicAttack:
                await HandleBasicAttackAsync(packet, cancellationToken);
                break;
            case Opcodes.Revive:
            case Opcodes.ReviveLegacy:
                await HandleReviveAsync(packet, cancellationToken);
                break;
            case Opcodes.Kitbag:
            case Opcodes.Storage:
            case Opcodes.MoveItem:
            case Opcodes.Sell:
                // The same opcode carries an inventory move and the corpse
                // click that opens a loot window, so the loot source is claimed
                // before the log-only inventory path. A Wonderland boss corpse
                // sends its own 20-byte click while every other corpse uses the
                // 16-byte ground descriptor, so both are offered the packet.
                if (await TryHandleWonderlandNativeLootPickupAsync(
                        packet,
                        cancellationToken))
                {
                    break;
                }

                if (await TryHandleGroundLootSourceAsync(
                        packet,
                        cancellationToken))
                {
                    break;
                }

                LogInventoryPacket(packet);
                break;
            case Opcodes.PickupDrops:
                await HandleMonsterLootPickupAsync(packet, cancellationToken);
                break;
            case Opcodes.UseOrEquip:
                await HandleUseOrEquipAsync(packet, cancellationToken);
                break;
            case Opcodes.BagItemAction:
                await HandleBagItemActionAsync(packet, cancellationToken);
                break;
            case Opcodes.ItemInfoRequest:
                await HandleItemInfoRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.ForgeSelection:
                HandleForgeSelection(packet);
                await SendForgeStagingHintAsync(cancellationToken); // FINALISE
                break;
            case Opcodes.ForgeStart:
                await HandleForgeStartAsync(packet, cancellationToken);
                break;
            case Opcodes.ForgeCancel:
                // The stock Gear Mentor emits this ordinary-forge cancel when
                // gear is unequipped into the bag while its dialog is open.
                // Its subsequent 10193 item selections still belong to the
                // active Gear Mentor workflow, so only ordinary forge state is
                // invalidated here.
                ClearForgeSelection();
                break;
            case Opcodes.ForgeReplacementSelection when AmethystTransformEnabled:
                ClearForgeSelection();
                HandleAmethystTransformSelection(packet);
                break;
            case Opcodes.ForgeReplacementAction when AmethystTransformEnabled:
                ClearForgeSelection();
                await HandleAmethystTransformActionAsync(packet, cancellationToken);
                break;
            case Opcodes.ForgeReplacementSelection:
            case Opcodes.ForgeReplacementAction:
                ClearForgeSelection();
                Console.WriteLine(
                    $"[forge] ignored unsupported {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode}");
                break;
            case Opcodes.NpcDialogOpen:
                await HandleNpcDialogOpenAsync(packet, cancellationToken);
                break;
            case Opcodes.NpcDialogPageRequest:
                await HandleNpcDialogPageRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.NpcFunctionAction:
                if (IsGmPanelFrame(packet))
                {
                    await HandleGmPanelFrameAsync(packet, cancellationToken);
                    break;
                }

                await HandleNpcFunctionActionAsync(packet, cancellationToken);
                break;
            case Opcodes.MallCatalog:
                await HandleMallCatalogRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.MallPurchase:
                await HandleMallPurchaseAsync(packet, cancellationToken);
                break;
            case Opcodes.NpcShopPurchase: await HandleCapitalNpcShopPurchaseAsync(packet, cancellationToken); break;
            case Opcodes.QuestAppraisal:
                await HandleQuestAppraisalAsync(packet, cancellationToken);
                break;
            case Opcodes.QuestSelection:
                await HandleQuestSelectionAsync(packet, cancellationToken);
                break;
            case Opcodes.QuestAction:
                await HandleQuestActionAsync(packet, cancellationToken);
                break;
            case Opcodes.QuestSceneQuery:
                await HandleQuestSceneQueryAsync(packet, cancellationToken);
                break;
            case Opcodes.QuestAccepted:
                await HandleQuestHandInAsync(packet, cancellationToken);
                break;
            case Opcodes.QuestActionPair:
                await HandleQuestActionPairAsync(packet, cancellationToken);
                break;
            case Opcodes.GearEnhancerItemSelection:
                HandleGearEnhancerItemSelection(packet);
                break;
            case Opcodes.PlayerNameInspectRequest:
                await _session.SendAsync(packet.Buffer, cancellationToken, "PlayerNameInspectAck");
                break;
            case Opcodes.PlayerInspectRequest:
                await HandlePlayerInspectRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.PlayerInspectVisualRequest:
                await HandlePlayerInspectVisualRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.PetDeleteRequest:
                await HandlePetPresenceRequestAsync(
                    packet,
                    PetPresenceOperation.Delete,
                    cancellationToken);
                break;
            case Opcodes.PetTakeRequest:
                await HandlePetPresenceRequestAsync(
                    packet,
                    PetPresenceOperation.Take,
                    cancellationToken);
                break;
            case Opcodes.PetCallOutRequest:
                await HandlePetPresenceRequestAsync(
                    packet,
                    PetPresenceOperation.CallOut,
                    cancellationToken);
                break;
            case Opcodes.PetRecallRequest:
                await HandlePetPresenceRequestAsync(
                    packet,
                    PetPresenceOperation.Recall,
                    cancellationToken);
                break;
            case Opcodes.PetQuestDispatchRequest: // W3 Magic Bell (F-136)
                await HandlePetQuestDispatchAsync(packet, cancellationToken);
                break;
            case Opcodes.PetRenameRequest: // ITEMS-HERMES F-55
                await HandlePetRenameRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.PetOwnerMergeRequest:
                await HandlePetOwnerMergeRequestAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.PackedPetDetailRequest:
                await HandlePackedPetDetailRequestAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.PetWorkStartRequest: // PETWORK (F-137)
                await HandlePetWorkStartAsync(packet, cancellationToken);
                break;
            case Opcodes.PetWorkStatusRequest: // PETWORK (F-137)
                await HandlePetWorkStatusRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.PetWorkStopRequest: // PETWORK (F-137)
                await HandlePetWorkStopAsync(packet, cancellationToken);
                break;
            case Opcodes.PetToPetMergeRequest:
                if (await RefusePetActionWhileWorkingAsync(null, "pet_to_pet_merge", cancellationToken)) break; // PETWORK
                await HandlePetToPetMergeRequestAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.PetSoulContractRequest:
                await HandlePetSoulContractRequestAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.PetRebirthRequest:
                await HandlePetRebirthRequestAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.PetLevelUpgradeRequest:
                if (TryReadPetId(packet, out var upgradePetId) &&
                    await RefusePetActionWhileWorkingAsync(upgradePetId, "pet_level_upgrade", cancellationToken)) break; // PETWORK
                await HandlePetLevelUpgradeAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.Zodiac:
                await HandleZodiacAsync(packet, cancellationToken);
                break;
            case Opcodes.BreakItem:
                await HandleBreakItemAsync(packet, cancellationToken);
                break;
            case Opcodes.StorageItem:
                await HandleStorageItemAsync(packet, cancellationToken);
                break;
            case Opcodes.WarehouseTransfer:
                await HandleWarehouseTransferAsync(packet, cancellationToken);
                break;
            case Opcodes.ServerTimeRequest:
                await _session.SendAsync(
                    PacketBuilder.ServerTime(_realmCalendar),
                    cancellationToken,
                    "ServerTime");
                break;
            case Opcodes.ClientReady:
                await HandleClientReadyAsync(cancellationToken);
                break;
            case Opcodes.PlayerDetailRequest:
                await HandlePlayerDetailRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.FashionEffectVisibility:
                await HandleFashionEffectVisibilityAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.EnterUiReady:
                // W3 autoloot: the client also sends 10357 when its autoloot countdown reaches zero. That one is only
                // a time query: re-running the login extras repeated the "quest completed" chat line (P5 rig, run al8).
                // The client sends 10357 after every map change too, so only a 10357 near the time this session told
                // the client autoloot ends counts as the expiry ping.
                if (_enterUiReadyReceived && _postEnterBootstrapSent && IsAutolootExpiryPing())
                {
                    Console.WriteLine($"[autoloot] expiry ping character={_character?.Name ?? "<none>"}");
                    await SendStoredAutolootAsync(cancellationToken);
                    break;
                }
                _enterUiReadyReceived = true;
                Console.WriteLine($"[game] EnterUiReady character={_character?.Name ?? "<none>"}");
                // The panel's guild rows are read before the status bootstrap
                // below, so the first 10166 of the session already carries the
                // duty and contribution the character logs in with.
                if (_character is { } entered)
                {
                    await RefreshGuildPanelStatusAsync(
                        entered.Id,
                        cancellationToken);
                }
                await SendPostEnterBootstrapAsync(cancellationToken);
                await SendQuickEquipWornIdentitiesAsync(cancellationToken);
                // A guild member's client only knows its guild from the guild
                // messages, so the roster is published here: the window then
                // shows the guild without the player visiting the registrar.
                await SendGuildWindowAsync(cancellationToken);
                // A quest whose objectives are already met is published only now:
                // the client crashed dereferencing npc zero while the frame arrived
                // ahead of the world entry, and the reference only ever sends it in
                // the middle of play, right behind the kill that finished the quest.
                await SendLoginQuestProgressAsync(cancellationToken);
                await SendEventBoardAsync(cancellationToken); // EVENTSBOARD
                // F-138: the client drops a 10308 SYN sent before EnterUiReady (rig, PR 88), so it goes here.
                await SendRookieGrowthSyncAsync(cancellationToken);
                await SendStoredAutolootAsync(cancellationToken); // W3 autoloot time left (only when > 0)
                await TrySendOfflinePrizeAsync(cancellationToken); // F-131 offline prize (once per connection)
                await SendProfessionStateAfterUiReadyAsync(cancellationToken); // PROF slice 1: 10334/10336/10339
                await ResumePetWorkAfterEnterAsync(cancellationToken); // PETWORK: pay offline-finished work, watch the rest
                break;
            case Opcodes.GameServerReady:
            case Opcodes.GameServerInfo:
            case Opcodes.PlayerInspectFollowup:
            case 10192:
                Console.WriteLine($"[game] ignored {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode}");
                break;
            // The guild window asks for its guild every time it is opened or
            // refreshed. The request carries no fields, so the answer is the
            // whole guild again - base info plus the member rows, whose online
            // flags are what the window counts for its "online / cap" line.
            case Opcodes.ConsortiaInfoRequest:
                Console.WriteLine(
                    $"[guild] window request character={_character?.Name ?? "<none>"}");
                // The List tab's content is a separate message and is sent even to
                // a character with no guild of its own.
                await SendGuildListAsync(cancellationToken);
                await SendGuildWindowAsync(cancellationToken);
                break;
            // The client sells with 10060, not the known-but-unused 10053.
            case Opcodes.SellItem:
                await HandleSellItemRequestAsync(packet, cancellationToken);
                break;
            // The guild registrar's create request, read off the client.
            case GuildRegistrarProtocol.CreateRequest:
                await HandleGuildCreateRequestAsync(packet, cancellationToken);
                break;
            // The guild window's leave action, read off the client.
            case Opcodes.ConsortiaExit:
                await HandleGuildExitRequestAsync(packet, cancellationToken);
                break;
            // The guild window's proclamation edit, read off the client.
            case Opcodes.ConsortiaText:
                await HandleGuildTextRequestAsync(packet, cancellationToken);
                break;
            // The guild window's member management, read off the client.
            case GuildMemberActionProtocol.DutyRequest:
                await HandleGuildDutyRequestAsync(packet, cancellationToken);
                break;
            case GuildMemberActionProtocol.MemberDelRequest:
                await HandleGuildMemberDelRequestAsync(packet, cancellationToken);
                break;
            // The guild window's "Refuse the application?" box, which sends its
            // new state as a lone dword.
            case Opcodes.ConsortiaRefuseApplications:
                await HandleGuildRefuseApplicationsRequestAsync(
                    packet,
                    cancellationToken);
                break;
            // The List tab's Apply button: the selected guild's key is the body's
            // leading dword.
            case Opcodes.ConsortiaInviteConfirm:
                await HandleGuildApplyRequestAsync(packet, cancellationToken);
                break;
            // An officer answering an application: the applicant's name is the
            // body's string field and the leading dword was zero when measured.
            case Opcodes.ConsortiaInvite:
                await HandleGuildApplicationAnswerAsync(
                    packet,
                    cancellationToken);
                break;
            // A member inside a running instance names another character, who
            // then receives the party's own Enter window.
            case Opcodes.RepetitionInvitation:
                await HandleInstanceInvitationRequestAsync(
                    packet,
                    cancellationToken);
                break;
            default:
                Console.WriteLine(
                    $"[game] unknown {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} {packet.ToHexPreview()}");
                // Pet-work request recovery: probe candidate work opcodes by
                // naming the raw frame so the real one can be identified from
                // a single in-game click instead of a guess.
                if (packet.Opcode is >= 10240 and <= 10299)
                {
                    Console.WriteLine(
                        $"[game] pet-probe opcode={packet.Opcode} " +
                        $"len={packet.Length} " +
                        $"frame={Convert.ToHexString(packet.Buffer)} " +
                        $"payload={Convert.ToHexString(packet.Payload)}");
                }
                DumpUnknownPacketWords(packet);
                break;
        }
    }

    /// <summary>
    /// Prints every word of a packet the server does not handle, so a wire format
    /// can be read straight off the log.
    /// </summary>
    /// <remarks>
    /// Protocol work needs the whole packet: the bounded preview above is enough to
    /// recognise a packet, not to read its fields. This is instrumentation for
    /// reading a format, so it is bounded and only runs for opcodes that reach the
    /// default branch.
    /// </remarks>
    private static void DumpUnknownPacketWords(GamePacket packet)
    {
        const int Limit = 512;
        var payload = packet.Payload;
        var length = Math.Min(payload.Length, Limit);
        Console.WriteLine(
            $"[game] unknown-words opcode={packet.Opcode} payload={payload.Length}" +
            (payload.Length > Limit ? $" (first {Limit} bytes)" : string.Empty));
        for (var offset = 0; offset < length; offset += 16)
        {
            var span = payload.Slice(offset, Math.Min(16, length - offset));
            var words = new List<string>();
            for (var index = 0; index + sizeof(int) <= span.Length;
                 index += sizeof(int))
            {
                words.Add(
                    System.Buffers.Binary.BinaryPrimitives
                        .ReadInt32LittleEndian(span.Slice(index, sizeof(int)))
                        .ToString());
            }

            Console.WriteLine(
                $"    +{offset:D4} {Convert.ToHexString(span)}  " +
                $"[{string.Join(' ', words)}]");
        }
    }

}
