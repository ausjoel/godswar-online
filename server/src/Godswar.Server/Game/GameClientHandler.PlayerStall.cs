using Godswar.Server.Infrastructure.Trade;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// TRADE: player stalls (StallSellUI / StallBuyUI; IGG guide acid=43: "the stall window can be
/// accessed from your inventory screen"). The seller prices bag items (10062), shows the stall
/// (10061 State 1, with a sign text), and others click him to browse (10065) and buy (10066).
/// Items stay in the seller's bag until a purchase commits in one transaction, so closing the
/// stall, logging out or a crash leaves every item where it was.
/// Generated rule (review finding 10): browsing and buying both need the buyer within
/// <see cref="PlayerTradeRange"/> (30 units) of the seller, the same generated range as trade;
/// the guide gives no stall range. A buyer who pays Gold must be level 65+ (the trade rule for
/// the side giving Gold, client text TradeBijouLimit).
/// Gate: server_rates <c>trade.stalls</c> = 1.
/// </summary>
internal sealed partial class GameClientHandler
{
    // Client text StallSell_2: "Stall can not be more than 200 million total articles"; the
    // StallCounterUI check (0x61c918) caps unit price x stack at 200,000,000.
    internal const long PlayerStallMaximumTotal = 200_000_000;

    private async Task HandlePlayerStallPacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        switch (packet.Opcode)
        {
            case PacketBuilder.PlayerStallOpcode: await HandlePlayerStallStateAsync(packet, cancellationToken); break;
            case PacketBuilder.PlayerStallAddItemOpcode: await HandlePlayerStallAddItemAsync(packet, cancellationToken); break;
            case PacketBuilder.PlayerStallDeleteItemOpcode: await HandlePlayerStallDeleteItemAsync(packet, cancellationToken); break;
            case PacketBuilder.PlayerStallItemOpcode: await HandlePlayerStallBrowseAsync(packet, cancellationToken); break;
            case PacketBuilder.PlayerStallBuyItemOpcode: await HandlePlayerStallBuyAsync(packet, cancellationToken); break;
        }
    }

    private async Task HandlePlayerStallStateAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!PlayerTradeRequests.TryReadStall(packet.Buffer, out var request)) return;
        if (!request.Open)
        {
            var closed = PlayerTradeBook.Shared.CloseStall(character.Id);
            if (closed is null) return;
            Console.WriteLine($"[stall] closed by {character.Name}");
            await PlayerTradeWatchdog.BroadcastStallStateAsync(_registry, closed.Seller, false, string.Empty, cancellationToken);
            await ReloadAfterPlayerTradeAsync("stall-close", cancellationToken);
            return;
        }
        if (!TryBuildOwnTradeParty(out var self)) return;
        if (character.CurrentHp <= 0)
        {
            await SendTradeNoteAsync("You cannot set up a stall while dead.", cancellationToken);
            return;
        }
        var text = new string(request.Text.Where(static c => !char.IsControl(c)).ToArray()).Trim();
        if (PlayerTradeBook.Shared.OpenStall(self, text, out var stall) != PlayerTradeRefusal.None || stall is null)
        {
            await SendTradeNoteAsync("Finish your trade first.", cancellationToken);
            await _session.SendAsync(
                PacketBuilder.PlayerStall(PacketBuilder.TradeObjectIdFor(self.ObjectId, true), false, string.Empty, self.Name),
                cancellationToken, "PlayerStallRefused");
            return;
        }
        Console.WriteLine($"[stall] opened by {character.Name} goods={stall.Goods.Count} text=\"{text}\"");
        await PlayerTradeWatchdog.BroadcastStallStateAsync(_registry, self, true, text, cancellationToken);
    }

    private async Task HandlePlayerStallAddItemAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!PlayerTradeRequests.TryReadStallAddItem(packet.Buffer, out var request)) return;
        if (!TryBuildOwnTradeParty(out var self)) return;
        string? refusal = null;
        PlayerStallGoods? goods = null;
        if (request.Silver < 0 || request.Gold < 0 || (request.Silver == 0 && request.Gold == 0))
            refusal = "Set a price first.";
        var probe = refusal is null
            ? await ProbeTradableKitBagItemAsync(request.BagPage, request.BagGrid, cancellationToken)
            : default;
        refusal ??= probe.Refusal;
        if (refusal is null &&
            ((long)request.Silver * probe.Offer!.Stack > PlayerStallMaximumTotal ||
             (long)request.Gold * probe.Offer.Stack > PlayerStallMaximumTotal))
        {
            refusal = "Stall can not be more than 200 million total articles";
        }
        if (refusal is null)
        {
            refusal = PlayerTradeBook.Shared.AddGoods(
                    self, request.Pocket, request.StallIndex, probe.Offer!, request.Silver, request.Gold, out goods) switch
            {
                PlayerTradeRefusal.None => null,
                PlayerTradeRefusal.RequesterBusy => "Finish your trade first.",
                PlayerTradeRefusal.SlotAlreadyOffered => "That item is already on show.",
                PlayerTradeRefusal.GoodsReserved => "Someone is buying that item right now.",
                _ => "That stall slot cannot be used."
            };
        }
        if (refusal is not null || goods is null)
        {
            await SendTradeNoteAsync(refusal ?? "That item cannot be sold.", cancellationToken);
            await SendKitBagRefreshAsync(cancellationToken); // clears the client's stall lock on the bag item
            return;
        }
        Console.WriteLine($"[stall] {character.Name} shows item={goods.Offer.ItemId} x{goods.Offer.Stack} slot={goods.Offer.KitBagSlot} at {request.Pocket}/{request.StallIndex} silver={request.Silver} gold={request.Gold} goods={goods.GoodsId}");
        await _session.SendAsync(
            PacketBuilder.PlayerStallAddItem(request.StallIndex, request.Pocket, request.BagPage, request.BagGrid,
                request.Silver, request.Gold, goods.GoodsId),
            cancellationToken, "PlayerStallAddItem");
    }

    private async Task HandlePlayerStallDeleteItemAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!PlayerTradeRequests.TryReadStallDeleteItem(packet.Buffer, out var stallIndex, out var pocket)) return;
        var result = PlayerTradeBook.Shared.RemoveGoods(character.Id, pocket, stallIndex);
        if (result == PlayerTradeRefusal.GoodsReserved)
        {
            await SendTradeNoteAsync("Someone is buying that item right now.", cancellationToken);
            return;
        }
        await _session.SendAsync(PacketBuilder.PlayerStallDeleteItem(stallIndex, pocket), cancellationToken, "PlayerStallDeleteItem");
    }

    private async Task HandlePlayerStallBrowseAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (!PlayerTradeRequests.TryReadStallBrowse(packet.Buffer, out var request)) return;
        if (!TryResolveMapPlayer(request.SellerObjectId, request.SellerName, out var seller) ||
            seller.CharacterId == _character!.Id)
        {
            return;
        }
        var stall = PlayerTradeBook.Shared.FindStall(seller.CharacterId);
        if (stall is null || !stall.Open)
        {
            // The client still shows a sign that is gone: take it down for this viewer.
            await _session.SendAsync(PacketBuilder.PlayerStall(seller.ObjectId, false, string.Empty, seller.CharacterName),
                cancellationToken, "PlayerStallGone");
            return;
        }
        if (Distance(_character, seller.Character) > PlayerTradeRange)
        {
            await SendTradeNoteAsync($"{seller.CharacterName} is too far away.", cancellationToken);
            return;
        }
        var live = await PlayerTradeStore.ReadKitBagInstanceIdsAsync(seller.CharacterId, cancellationToken);
        PlayerTradeBook.Shared.DropGoodsNotIn(seller.CharacterId, live);
        var goods = PlayerTradeBook.Shared.SnapshotGoods(seller.CharacterId);
        foreach (var item in goods)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerStallItem(seller.ObjectId, seller.CharacterName, item.GoodsId, item.Pocket,
                    item.StallIndex, item.SilverPerUnit, item.GoldPerUnit, item.Offer.Item),
                cancellationToken, "PlayerStallItem");
        }
        Console.WriteLine($"[stall] {_character.Name} browsed {seller.CharacterName}: {goods.Count} item(s)");
    }

    private async Task HandlePlayerStallBuyAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!PlayerTradeRequests.TryReadStallBuy(packet.Buffer, out var request)) return;
        if (!TryResolveMapPlayer(request.SellerObjectId, request.SellerName, out var seller) ||
            seller.CharacterId == character.Id)
        {
            await SendTradeNoteAsync("That stall is gone.", cancellationToken);
            return;
        }
        if (Distance(character, seller.Character) > PlayerTradeRange)
        {
            await SendTradeNoteAsync($"{seller.CharacterName} is too far away.", cancellationToken);
            return;
        }
        if (PlayerTradeBook.Shared.IsTrading(character.Id))
        {
            await SendTradeNoteAsync("Finish your trade first.", cancellationToken);
            return;
        }
        var reserve = PlayerTradeBook.Shared.ReserveGoods(seller.CharacterId, request.GoodsId, request.Pocket, request.StallIndex, out var goods);
        if (reserve != PlayerTradeRefusal.None || goods is null)
        {
            await SendTradeNoteAsync(reserve == PlayerTradeRefusal.GoodsReserved
                ? "Someone is buying that item right now."
                : "That item is no longer on sale.", cancellationToken);
            return;
        }
        var quantity = request.Quantity <= 0 ? goods.Offer.Stack : request.Quantity;
        if (quantity > goods.Offer.Stack)
        {
            PlayerTradeBook.Shared.ReleaseGoods(seller.CharacterId, goods, 0);
            await SendTradeNoteAsync("There are not that many on sale.", cancellationToken);
            return;
        }
        if (goods.GoldPerUnit > 0 && character.Level < PlayerTradeGoldMinimumLevel)
        {
            PlayerTradeBook.Shared.ReleaseGoods(seller.CharacterId, goods, 0);
            await SendTradeNoteAsync("Only Level 65+ player characters are able to make a deal using Gold.", cancellationToken);
            return;
        }

        // Review finding 4: the reservation is always ended, even if the store throws.
        PlayerTradeCommitResult result;
        var released = false;
        try
        {
            result = await PlayerTradeStore.CommitStallPurchaseAsync(
                seller.AccountId, seller.CharacterId,
                new PlayerTradeItemRef(goods.Offer.InstanceId, goods.Offer.KitBagSlot, goods.Offer.ItemId, goods.Offer.Stack,
                    goods.Offer.Fingerprint),
                _account!.Id, character.Id, quantity, goods.SilverPerUnit, goods.GoldPerUnit, CancellationToken.None);
            PlayerTradeBook.Shared.ReleaseGoods(seller.CharacterId, goods, result.Committed ? quantity : 0);
            released = true;
        }
        finally
        {
            if (!released) PlayerTradeBook.Shared.ReleaseGoods(seller.CharacterId, goods, 0);
        }
        Console.WriteLine(
            $"[stall] purchase {(result.Committed ? "committed" : "failed")} log={result.LogId} status={result.Status} detail={result.Detail} " +
            $"buyer={character.Id} seller={seller.CharacterId} item={goods.Offer.ItemId} qty={quantity} silver={goods.SilverPerUnit} gold={goods.GoldPerUnit}");
        if (!result.Committed)
        {
            if (result.Status is PlayerTradeCommitStatus.ItemChanged or PlayerTradeCommitStatus.ItemBound)
                PlayerTradeBook.Shared.RemoveGoods(seller.CharacterId, goods.Pocket, goods.StallIndex);
            await SendTradeNoteAsync(result.Status switch
            {
                PlayerTradeCommitStatus.NotEnoughSilver => "You do not have enough silver.",
                PlayerTradeCommitStatus.NotEnoughGold => "Not enough Gold.",
                PlayerTradeCommitStatus.BagFull => result.CharacterId == character.Id ? "Your bag is full." : "Purchase failed.",
                PlayerTradeCommitStatus.ItemChanged or PlayerTradeCommitStatus.ItemBound => "That item is no longer on sale.",
                PlayerTradeCommitStatus.WalletOverflow => "That would exceed the money limit.",
                _ => "Purchase failed."
            }, cancellationToken);
            return;
        }

        // Committed: the seller's reload is queued whatever happens to the frames (finding 9/12).
        try
        {
            await PlayerTradeWatchdog.TrySendAsync(_session,
                PacketBuilder.PlayerStallBuyItem(seller.ObjectId, seller.CharacterName,
                    PacketBuilder.TradeObjectIdFor(CurrentPlayerObjectId, true), character.Name,
                    goods.GoodsId, goods.Pocket, goods.StallIndex, quantity),
                "PlayerStallBuyItem", CancellationToken.None);
            await PlayerTradeWatchdog.TrySendAsync(seller.Session,
                PacketBuilder.PlayerStallBuyItem(PacketBuilder.TradeObjectIdFor(seller.ObjectId, true), seller.CharacterName,
                    CurrentPlayerObjectId, character.Name, goods.GoodsId, goods.Pocket, goods.StallIndex, quantity),
                "PlayerStallSold", CancellationToken.None);
        }
        finally
        {
            PlayerTradeWatchdog.PostReload(_registry, seller.Session, "stall-sold");
        }
        await ReloadAfterPlayerTradeAsync("stall-buy", CancellationToken.None);
    }

    /// <summary>Called when another player comes into view: re-sends his stall sign.</summary>
    private async Task SendVisibleStallSignAsync(GameSessionContext player, CancellationToken cancellationToken)
    {
        if (!PlayerStallsEnabled) return;
        var stall = PlayerTradeBook.Shared.FindStall(player.CharacterId);
        if (stall is null || !stall.Open) return;
        await _session.SendAsync(
            PacketBuilder.PlayerStall(player.ObjectId, true, stall.Text, player.CharacterName),
            cancellationToken, "VisiblePlayerStallSign");
    }
}
