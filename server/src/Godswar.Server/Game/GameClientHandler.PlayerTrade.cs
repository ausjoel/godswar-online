using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Trade;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// TRADE: the player trade window (IGG guide acid=46): "Click on the player icon to send a request
/// for trade. The trading window will appear on both parties' interfaces ... drag the item ... type
/// the amount ... Both parties must click the Confirm button". The stock client has no Agree/Refuse
/// box for trade (its 10104 handler opens TradeUI straight away on State 1, as the 2008 server's
/// StartTrade did), so the target accepts by trading and refuses with Cancel (State 4).
/// Gate: server_rates <c>trade.player</c> = 1. Off, the opcodes keep reaching the default branch.
/// </summary>
internal sealed partial class GameClientHandler
{
    // Generated rule (not in the guide or the 2008 server, which had a "TODO: distance" comment):
    // both players must stand within this many map units of each other to start and to finish.
    internal const float PlayerTradeRange = 30f;

    // Client text TradeBijouLimit: "Only Level 65+ player characters are able to make a deal using Gold."
    // Applied to the side that GIVES Gold (trade offer, stall buyer). Whether the receiver must be
    // 65+ too is an open question for the admin (review finding 11/20); see the README.
    internal const int PlayerTradeGoldMinimumLevel = 65;

    // Generated rule (review finding 18): items handed out free by systems written before trade
    // existed, which the character could otherwise pool from alts. Only the Lelantine Farm wooden
    // tools (GENERATED handout, LelantineFarmToolPolicy) for now; extend after the admin decides.
    internal static readonly IReadOnlySet<uint> PlayerTradeRefusedItemIds = new HashSet<uint>
    {
        (uint)LelantineFarmToolPolicy.WoodenToolItemId
    };

    internal static bool PlayerTradeEnabled => ServerRates.Current.Get(ServerRateKeys.PlayerTrade) >= 1m;

    internal static bool PlayerStallsEnabled => ServerRates.Current.Get(ServerRateKeys.PlayerStalls) >= 1m;

    internal static bool IsPlayerTradeOpcode(ushort opcode) => opcode is
        PacketBuilder.PlayerTradeOpcode or PacketBuilder.PlayerTradeMoneyOpcode or
        PacketBuilder.PlayerTradeAddItemOpcode or PacketBuilder.PlayerTradeRemoveItemOpcode;

    internal static bool IsPlayerStallOpcode(ushort opcode) => opcode is
        PacketBuilder.PlayerStallOpcode or PacketBuilder.PlayerStallAddItemOpcode or
        PacketBuilder.PlayerStallDeleteItemOpcode or PacketBuilder.PlayerStallItemOpcode or
        PacketBuilder.PlayerStallBuyItemOpcode;

    /// <summary>Claims 10104-10108 and 10061-10066 only while their gate is on.</summary>
    internal static bool ClaimsPlayerTradePacket(ushort opcode) =>
        (IsPlayerTradeOpcode(opcode) && PlayerTradeEnabled) ||
        (IsPlayerStallOpcode(opcode) && PlayerStallsEnabled);

    private async Task<bool> TryHandlePlayerTradePacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (!ClaimsPlayerTradePacket(packet.Opcode)) return false;
        if (_character is null || _account is null) return true;
        PlayerTradeWatchdog.Ensure(_registry);
        try
        {
            if (!PlayerTradeStore.Enabled)
            {
                await SendTradeNoteAsync("Trading is not available right now.", cancellationToken);
                return true;
            }
            switch (packet.Opcode)
            {
                case PacketBuilder.PlayerTradeOpcode: await HandlePlayerTradeStateAsync(packet, cancellationToken); break;
                case PacketBuilder.PlayerTradeMoneyOpcode: await HandlePlayerTradeMoneyAsync(packet, cancellationToken); break;
                case PacketBuilder.PlayerTradeAddItemOpcode: await HandlePlayerTradeAddItemAsync(packet, cancellationToken); break;
                case PacketBuilder.PlayerTradeRemoveItemOpcode: await HandlePlayerTradeRemoveItemAsync(packet, cancellationToken); break;
                default: await HandlePlayerStallPacketAsync(packet, cancellationToken); break;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[trade] opcode={packet.Opcode} character={_character.Id} failed: {error.GetType().Name}: {error.Message}");
        }
        return true;
    }

    // ---- 10104 -------------------------------------------------------------------------------

    private async Task HandlePlayerTradeStateAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (!PlayerTradeRequests.TryReadTrade(packet.Buffer, out var request)) return;
        switch (request.State)
        {
            case PacketBuilder.PlayerTradeStateStart:
                await RequestPlayerTradeAsync(request, cancellationToken);
                break;
            case PacketBuilder.PlayerTradeStateLock:
                await LockPlayerTradeAsync(cancellationToken);
                break;
            case PacketBuilder.PlayerTradeStateCancel:
                await CancelPlayerTradeAsync("cancelled", cancellationToken);
                break;
            default:
                Console.WriteLine($"[trade] ignored state={request.State} from character={_character!.Id}");
                break;
        }
    }

    private async Task RequestPlayerTradeAsync(PlayerTradeRequests.TradeRequest request, CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!TryResolveMapPlayer(request.TargetObjectId, request.TargetName, out var target) ||
            target.CharacterId == character.Id)
        {
            await SendTradeNoteAsync("That player is not here.", cancellationToken);
            return;
        }
        if (!TryBuildOwnTradeParty(out var self))
        {
            return;
        }
        var other = TradePartyOf(target);
        var refusal = CheckTradeReach(self, character, other, target.Character);
        if (refusal is not null)
        {
            await SendTradeNoteAsync(refusal, cancellationToken);
            return;
        }

        var result = PlayerTradeBook.Shared.TryOpen(self, other, out var trade, out var dropped, Environment.TickCount64);
        if (result != PlayerTradeRefusal.None)
        {
            await SendTradeNoteAsync(result switch
            {
                PlayerTradeRefusal.TargetBusy => $"{other.Name} is busy.",
                PlayerTradeRefusal.RequestCooldown => $"{other.Name} declined; wait a few seconds before asking again.",
                _ => "You are already trading or running a stall."
            }, cancellationToken);
            return;
        }

        Console.WriteLine($"[trade] open id={trade!.Id} {self.Name}({self.CharacterId}) -> {other.Name}({other.CharacterId})");
        if (dropped.Count > 0)
        {
            // The requester's own priced-but-not-shown stall goods are forgotten; clear them from
            // his stall window (10063 is redraw-only on the client) and say so.
            foreach (var goods in dropped)
            {
                await _session.SendAsync(PacketBuilder.PlayerStallDeleteItem(goods.StallIndex, goods.Pocket),
                    cancellationToken, "PlayerStallGoodsDropped");
            }
            await SendTradeNoteAsync("Your stall prices were cleared because you started a trade.", cancellationToken);
        }
        await SendTradeStateToBothAsync(trade, trade.Requester, PacketBuilder.PlayerTradeStateStart, PacketBuilder.PlayerTradeStateStart, cancellationToken);
    }

    private async Task LockPlayerTradeAsync(CancellationToken cancellationToken)
    {
        var character = _character!;
        var refusal = PlayerTradeBook.Shared.Lock(character.Id, out var trade, out var commit);
        if (refusal == PlayerTradeRefusal.OfferJustChanged)
        {
            // Review finding 7: this OK may have been pressed on the old offer. The client resets
            // its own OK when the partner's change arrives (UNPROVEN on screen); the player presses
            // OK again after looking at the new offer.
            await SendTradeNoteAsync("The other player just changed the offer. Check it and press OK again.", cancellationToken);
            return;
        }
        if (refusal != PlayerTradeRefusal.None || trade is null) return;
        var side = trade.SideOf(character.Id);
        var partner = trade.PartnerOf(character.Id);
        if (!commit)
        {
            await SendTradeStateToBothAsync(
                trade,
                side,
                PacketBuilder.PlayerTradeStateLock,
                partner.Locked ? PacketBuilder.PlayerTradeStateLock : PacketBuilder.PlayerTradeStateStart,
                cancellationToken);
            return;
        }

        // This handler owns the commit. Whatever happens (a failed send, cancellation), the trade must
        // leave the Committing phase, or both players would stay "busy" until a restart.
        try
        {
            await SendTradeStateToBothAsync(trade, side, PacketBuilder.PlayerTradeStateLock,
                PacketBuilder.PlayerTradeStateLock, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[trade] lock frame failed id={trade.Id}: {error.Message}");
        }
        finally
        {
            try
            {
                await CommitPlayerTradeAsync(trade, cancellationToken);
            }
            finally
            {
                if (trade.Phase != PlayerTradePhase.Closed)
                {
                    PlayerTradeBook.Shared.Finish(trade);
                    Console.WriteLine($"[trade] commit interrupted id={trade.Id}; trade closed");
                    _ = PlayerTradeWatchdog.SendCancelAsync(_registry, trade, "Trade failed.", CancellationToken.None);
                }
            }
        }
    }

    private async Task CancelPlayerTradeAsync(string reason, CancellationToken cancellationToken)
    {
        var trade = PlayerTradeBook.Shared.Cancel(_character!.Id, false, byPlayer: true, Environment.TickCount64);
        if (trade is null) return;
        Console.WriteLine($"[trade] {reason} id={trade.Id} by={_character.Name}");
        await PlayerTradeWatchdog.SendCancelAsync(_registry, trade, null, cancellationToken);
        await ReloadAfterPlayerTradeAsync("trade-cancel", cancellationToken);
        PlayerTradeWatchdog.PostReload(_registry, trade.PartnerOf(_character.Id).Party.Session, "trade-cancel");
    }

    // ---- 10105 / 10107 / 10108 ------------------------------------------------------------------

    private async Task HandlePlayerTradeMoneyAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!PlayerTradeRequests.TryReadMoney(packet.Buffer, out var silver, out var gold)) return;
        var trade = PlayerTradeBook.Shared.Find(character.Id);
        if (trade is null) return;
        string? refusal = null;
        if (silver < 0 || gold < 0) refusal = "That amount cannot be traded.";
        else if (silver > character.Silver) refusal = "You do not have that much silver.";
        else if (gold > character.Gold) refusal = "Not enough Gold.";
        else if (gold > 0 && character.Level < PlayerTradeGoldMinimumLevel)
            refusal = "Only Level 65+ player characters are able to make a deal using Gold.";
        if (refusal is not null)
        {
            silver = 0;
            gold = 0;
        }
        if (PlayerTradeBook.Shared.SetMoney(character.Id, silver, gold, out trade) != PlayerTradeRefusal.None || trade is null)
            return;
        var self = trade.SideOf(character.Id).Party;
        var partner = trade.PartnerOf(character.Id).Party;
        if (refusal is not null)
        {
            await SendTradeNoteAsync(refusal, cancellationToken);
            // UNPROVEN: the 10105 handler writes the amounts into the trade of uiID, so uiID 0x1448
            // should reset the offerer's own field to 0.
            await _session.SendAsync(
                PacketBuilder.PlayerTradeMoney(PacketBuilder.TradeObjectIdFor(self.ObjectId, true), self.Name, 0, 0),
                cancellationToken, "PlayerTradeMoneyReset");
        }
        await SendToPartyAsync(partner, PacketBuilder.PlayerTradeMoney(self.ObjectId, self.Name, silver, gold), "PlayerTradeMoney", cancellationToken);
    }

    private async Task HandlePlayerTradeAddItemAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!PlayerTradeRequests.TryReadAddItem(packet.Buffer, out var request)) return;
        var trade = PlayerTradeBook.Shared.Find(character.Id);
        if (trade is null) return;
        var self = trade.SideOf(character.Id).Party;
        var offer = await ProbeTradableKitBagItemAsync(request.BagPage, request.BagGrid, cancellationToken);
        var refusal = offer.Refusal;
        PlayerTradeOffer? replaced = null;
        if (refusal is null)
        {
            var added = PlayerTradeBook.Shared.AddItem(character.Id, request.TradeSlot, offer.Offer!, out trade, out replaced);
            refusal = added switch
            {
                PlayerTradeRefusal.None => null,
                PlayerTradeRefusal.SlotAlreadyOffered => "That item is already in the trade.",
                PlayerTradeRefusal.BadSlot => "That trade slot cannot be used.",
                _ => "The trade is not open."
            };
        }
        if (refusal is not null || trade is null)
        {
            await SendTradeNoteAsync(refusal ?? "The trade is not open.", cancellationToken);
            // UNPROVEN: 10108 with uiID 0x1448 clears the slot the client already filled locally;
            // the bag refresh unlocks the bag item the client marked as in trade.
            if (request.TradeSlot is >= 0 and < PlayerTradeBook.TradeSlots)
            {
                await _session.SendAsync(
                    PacketBuilder.PlayerTradeRemoveItem(PacketBuilder.TradeObjectIdFor(self.ObjectId, true), self.Name, request.TradeSlot),
                    cancellationToken, "PlayerTradeItemRefused");
            }
            await SendKitBagRefreshAsync(cancellationToken);
            return;
        }
        var partner = trade.PartnerOf(character.Id).Party;
        await SendToPartyAsync(partner,
            PacketBuilder.PlayerTradeItem(self.ObjectId, self.Name, request.TradeSlot, offer.Offer!.Item),
            "PlayerTradeItem", cancellationToken);
    }

    private async Task HandlePlayerTradeRemoveItemAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!PlayerTradeRequests.TryReadRemoveItem(packet.Buffer, out var tradeSlot)) return;
        if (PlayerTradeBook.Shared.RemoveItem(character.Id, tradeSlot, out var trade) != PlayerTradeRefusal.None || trade is null)
            return;
        var self = trade.SideOf(character.Id).Party;
        var partner = trade.PartnerOf(character.Id).Party;
        await SendToPartyAsync(partner, PacketBuilder.PlayerTradeRemoveItem(self.ObjectId, self.Name, tradeSlot), "PlayerTradeRemoveItem", cancellationToken);
    }

    // ---- commit ------------------------------------------------------------------------------

    private async Task CommitPlayerTradeAsync(PlayerTradeSession trade, CancellationToken cancellationToken)
    {
        var a = trade.Requester;
        var b = trade.Target;
        string? refusal = null;
        if (!PlayerTradeWatchdog.IsPartyPresent(_registry, a.Party, out var aContext) ||
            !PlayerTradeWatchdog.IsPartyPresent(_registry, b.Party, out var bContext))
        {
            refusal = "Trade failed: the other player left.";
        }
        else if (Distance(aContext.Character, bContext.Character) > PlayerTradeRange)
        {
            refusal = "Trade failed: you are too far apart.";
        }
        else if ((a.Gold > 0 && aContext.Character.Level < PlayerTradeGoldMinimumLevel) ||
                 (b.Gold > 0 && bContext.Character.Level < PlayerTradeGoldMinimumLevel))
        {
            refusal = "Only Level 65+ player characters are able to make a deal using Gold.";
        }

        PlayerTradeCommitResult? result = null;
        if (refusal is null)
        {
            result = await PlayerTradeStore.CommitTradeAsync(ToCommitSide(a), ToCommitSide(b), CancellationToken.None);
            if (!result.Committed)
            {
                var who = result.CharacterId == a.Party.CharacterId ? a.Party.Name
                    : result.CharacterId == b.Party.CharacterId ? b.Party.Name : null;
                refusal = result.Status switch
                {
                    PlayerTradeCommitStatus.BagFull => $"Trade failed: {(who is null ? "a" : who + "'s")} bag is full.",
                    PlayerTradeCommitStatus.NotEnoughSilver => $"Trade failed: {who ?? "a player"} does not have that much silver.",
                    PlayerTradeCommitStatus.NotEnoughGold => $"Trade failed: {who ?? "a player"} does not have that much Gold.",
                    PlayerTradeCommitStatus.ItemBound => "Trade failed: a bound item cannot be traded.",
                    PlayerTradeCommitStatus.ItemChanged => "Trade failed: an offered item was moved or used.",
                    PlayerTradeCommitStatus.WalletOverflow => "Trade failed: that would exceed the money limit.",
                    _ => "Trade failed."
                };
            }
        }

        PlayerTradeBook.Shared.Finish(trade);
        var committed = refusal is null;
        Console.WriteLine(
            $"[trade] {(committed ? "committed" : "failed")} id={trade.Id} log={result?.LogId} " +
            $"status={result?.Status.ToString() ?? "precheck"} detail={result?.Detail ?? refusal} " +
            $"a={a.Party.CharacterId} items={a.Offers.Count} silver={a.Silver} gold={a.Gold} " +
            $"b={b.Party.CharacterId} items={b.Offers.Count} silver={b.Silver} gold={b.Gold}");
        // Review finding 9/12: once the result is known, the closing frames go out with no
        // cancellation (each send is guarded), and the partner's reload is queued whatever happens
        // to this session's sends.
        var reason = committed ? "trade-commit" : "trade-failed";
        var partnerSession = trade.PartnerOf(_character!.Id).Party.Session;
        try
        {
            if (committed)
            {
                await SendTradeStateToBothAsync(trade, a, PacketBuilder.PlayerTradeStateComplete,
                    PacketBuilder.PlayerTradeStateComplete, CancellationToken.None);
            }
            else
            {
                await PlayerTradeWatchdog.SendCancelAsync(_registry, trade, refusal, CancellationToken.None);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"[trade] closing frames failed id={trade.Id}: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            PlayerTradeWatchdog.PostReload(_registry, partnerSession, reason);
        }
        try
        {
            await ReloadAfterPlayerTradeAsync(reason, CancellationToken.None);
        }
        catch (Exception error)
        {
            Console.WriteLine($"[trade] own reload failed id={trade.Id}: {error.GetType().Name}: {error.Message}");
        }
    }

    private static PlayerTradeCommitSide ToCommitSide(PlayerTradeSide side) => new(
        side.Party.AccountId,
        side.Party.CharacterId,
        side.Silver,
        side.Gold,
        side.Offers.Values
            .Select(static o => new PlayerTradeItemRef(o.InstanceId, o.KitBagSlot, o.ItemId, o.Stack, o.Fingerprint))
            .ToArray());

    // ---- helpers -----------------------------------------------------------------------------

    private readonly record struct TradableProbe(PlayerTradeOffer? Offer, string? Refusal);

    /// <summary>Reads the kit-bag row behind a bag page/grid pair and refuses bound or unknown items.</summary>
    private async Task<TradableProbe> ProbeTradableKitBagItemAsync(int bagPage, int bagGrid, CancellationToken cancellationToken)
    {
        if (bagPage is < 0 or >= KitBagPageCount || bagGrid is < 0 or >= KitBagSlotsPerPage)
            return new(null, "That bag slot cannot be traded.");
        var slot = bagPage * KitBagSlotsPerPage + bagGrid;
        var item = KitBagSlots.GetItem(_character!.KitBag, slot);
        var probe = await PlayerTradeStore.ProbeKitBagItemAsync(_character.Id, slot, cancellationToken);
        if (item.IsEmpty || probe is null || probe.ItemId != item.Id || probe.Stack != item.Stack)
            return new(null, "That item has changed; try again.");
        if (probe.Bound != 0 || item.Bound != 0 || PlayerTradeRefusedItemIds.Contains(probe.ItemId))
            return new(null, "This item cannot be traded.");
        // The partner / stall buyers are shown the database row (probe.Item), whose fingerprint
        // the commit re-checks, not the in-memory copy.
        return new(new PlayerTradeOffer(slot, probe.InstanceId, probe.ItemId, probe.Stack, probe.Fingerprint, probe.Item), null);
    }

    private bool TryBuildOwnTradeParty(out PlayerTradeParty party)
    {
        party = default!;
        if (!_registry.TryGetCharacterContext(_character!.Id, out var context) ||
            !ReferenceEquals(context.Session, _session))
        {
            return false;
        }
        party = TradePartyOf(context);
        return true;
    }

    private PlayerTradeParty TradePartyOf(GameSessionContext context) => new(
        context.CharacterId,
        context.AccountId,
        context.CharacterName,
        context.Session,
        context.ObjectId,
        context.MapId,
        context.WorldInstanceId,
        _registry.GetPlayerLifeRevision(context.Session),
        context.Character.Level);

    private static string? CheckTradeReach(
        PlayerTradeParty self, GameCharacter selfCharacter, PlayerTradeParty other, GameCharacter otherCharacter)
    {
        if (selfCharacter.CurrentHp <= 0) return "You cannot trade while dead.";
        if (otherCharacter.CurrentHp <= 0) return $"{other.Name} cannot trade right now.";
        if (self.MapId != other.MapId || self.WorldInstanceId != other.WorldInstanceId) return "That player is not here.";
        if (Distance(selfCharacter, otherCharacter) > PlayerTradeRange) return $"{other.Name} is too far away.";
        return null;
    }

    internal static float Distance(GameCharacter a, GameCharacter b)
    {
        var dx = a.PositionX - b.PositionX;
        var dz = a.PositionZ - b.PositionZ;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private async Task SendTradeStateToBothAsync(
        PlayerTradeSession trade, PlayerTradeSide subject, int state, int partnerState, CancellationToken cancellationToken)
    {
        var other = subject == trade.Requester ? trade.Target : trade.Requester;
        foreach (var recipient in new[] { subject.Party, other.Party })
        {
            var frame = PacketBuilder.PlayerTrade(
                PacketBuilder.TradeObjectIdFor(subject.Party.ObjectId, recipient.CharacterId == subject.Party.CharacterId),
                subject.Party.Name,
                PacketBuilder.TradeObjectIdFor(other.Party.ObjectId, recipient.CharacterId == other.Party.CharacterId),
                other.Party.Name,
                state,
                partnerState);
            await SendToPartyAsync(recipient, frame, "PlayerTradeState", cancellationToken);
        }
    }

    // Guarded for both parties (review finding 12): a closing session never stops the frame to
    // the other player.
    private static Task SendToPartyAsync(PlayerTradeParty recipient, byte[] frame, string label, CancellationToken cancellationToken) =>
        PlayerTradeWatchdog.TrySendAsync(recipient.Session, frame, label, cancellationToken);

    private Task SendTradeNoteAsync(string message, CancellationToken cancellationToken) =>
        _session.SendAsync(PacketBuilder.ServerNote(message), cancellationToken, "PlayerTradeNote");

    /// <summary>
    /// Re-reads this character's bag and wallet after a trade or stall sale moved them, then
    /// repaints the bag and the 10166 money fields. Runs on this handler (directly, or through the
    /// operator sink when another session asks).
    /// </summary>
    private async Task<string> ReloadAfterPlayerTradeAsync(string reason, CancellationToken cancellationToken)
    {
        if (_character is null || _account is null) return "not in the world";
        var previousKitBag = _character.KitBag ?? string.Empty;
        var snapshot = await _characterSnapshots.ReadAsync(_account.Id, _processRealmId, cancellationToken);
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot);
        if (hydrated is null || hydrated.Character.Id != _character.Id)
        {
            return "reload failed";
        }
        _character.KitBag = hydrated.Character.KitBag;
        _character.Silver = hydrated.Character.Silver;
        _character.Gold = hydrated.Character.Gold;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        foreach (var acknowledgement in PacketBuilder.KitBagMutationDeletionAcknowledgements(previousKitBag, _character.KitBag))
        {
            await _session.SendAsync(acknowledgement, cancellationToken, "PlayerTradeKitBagDeleteAck");
        }
        await SendKitBagRefreshAsync(cancellationToken);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "PlayerTradeWalletStatus");
        Console.WriteLine($"[trade] reloaded {_character.Name} reason={reason} silver={_character.Silver} gold={_character.Gold}");
        return "bag and wallet refreshed";
    }
}

/// <summary>Asks another session's handler to reload after a trade (runs under that handler's gate).</summary>
internal sealed record PlayerTradeReloadAction(string Reason) : OperatorSessionAction("trade");
