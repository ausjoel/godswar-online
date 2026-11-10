using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The capital shop service whose window is currently open, if any. The
    /// client's sell request carries no NPC identity, so sales are authorized
    /// against the last opened shop for this session.
    /// </summary>
    private CapitalNpcServiceKind? _openCapitalShopService;

    private async Task<bool> TryHandleCapitalNpcDialogOpenAsync(
        NpcSpawnDefinition npc,
        int questFlags,
        CancellationToken cancellationToken)
    {
        if (!CapitalNpcServiceProtocol.TryResolve(npc, out var service) ||
            service == CapitalNpcServiceKind.ExchangeMentor)
        {
            return false;
        }

        byte[] packet;
        if (service == CapitalNpcServiceKind.TeachingManager)
        {
            packet = PacketBuilder.NpcDescriptionDialogOpenAck(
                npc.InteractionId,
                npc.NpcKey,
                questFlags);
        }
        else if (service == CapitalNpcServiceKind.Mall)
        {
            // Captured on the reference: the mall npc opens with flags 0x200 and
            // then offers its two services through the function menu.
            packet = PacketBuilder.NpcMallDialogOpenAck(
                npc.InteractionId,
                questFlags);
        }
        else if (CapitalNpcServiceProtocol.IsShop(service))
        {
            packet = PacketBuilder.NpcShopDialogOpenAck(
                npc.InteractionId,
                npc.NpcKey,
                questFlags);
        }
        else if (service == CapitalNpcServiceKind.PrizeChest)
        {
            packet = PacketBuilder.NpcPrizeChestDialogOpenAck(
                npc.InteractionId,
                npc.NpcKey,
                questFlags);
        }
        else if (CapitalNpcServiceProtocol.TryGetDialogueRoutes(
                     npc,
                     out var routes))
        {
            packet = PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                routes[0].DialogIndex,
                npc.NpcKey);
        }
        else
        {
            return false;
        }
        await _session.SendAsync(
            packet,
            cancellationToken,
            "CapitalNpcDialogOpenAck");
        // The sell request carries no NPC identity, so the open shop is kept for
        // the duration of the conversation to authorize and price sales.
        _openCapitalShopService = CapitalNpcServiceProtocol.IsShop(service)
            ? service
            : null;
        Console.WriteLine(
            $"[npc] capital open npc={npc.InteractionId} " +
            $"key={npc.NpcKey} service={service}");
        return true;
    }

    /// <summary>
    /// Sells the whole kit bag stack the client addressed. The observed request
    /// is four bytes (bag page, index in page) with no quantity, so the stack is
    /// sold entirely and silver is credited in the same transaction.
    /// </summary>
    private async Task HandleSellItemRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        if (_openCapitalShopService is null)
        {
            Console.WriteLine(
                $"[npc-shop] ignored sale without an open shop " +
                $"character={_character.Name} len={packet.Length}");
            return;
        }

        if (!TryReadSellItemRequest(packet.Payload, out var sourceSlot))
        {
            Console.Error.WriteLine(
                "[npc-shop] rejected malformed sale " +
                $"character={_character.Name} len={packet.Length} " +
                $"bytes={packet.ToHexPreview()}");
            return;
        }

        CapitalShopSaleResult result;
        try
        {
            result = await _capitalShopPurchases.SellCapitalShopItemAsync(
                _account.Id,
                _character.Id,
                Guid.NewGuid(),
                sourceSlot,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A rejected sale must never drop the session; the transaction has
            // already rolled back, so the character is unchanged.
            Console.Error.WriteLine(
                $"[npc-shop] sale failed character={_character.Name} " +
                $"slot={sourceSlot} {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!result.Sold || result.Character is null)
        {
            Console.WriteLine(
                $"[npc-shop] sale rejected character={_character.Name} " +
                $"slot={sourceSlot} item={result.ItemId} " +
                $"quantity={result.Quantity} status={result.Status}");
            return;
        }

        InstallCapitalShopProjection(result.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "NpcShopSaleWalletStatus");
        // The stock client keeps painting an emptied slot from a full detail
        // refresh, so the sold slot is explicitly acknowledged as removed first.
        // This mirrors the clear-bag flow, which sends the same per-slot delete
        // acknowledgement before repainting.
        await _session.SendAsync(
            PacketBuilder.StorageItemKitBagDelete(sourceSlot),
            cancellationToken,
            "NpcShopSaleKitBagDelete");
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[npc-shop] sold character={_character.Name} " +
            $"slot={sourceSlot} item={result.ItemId} " +
            $"quantity={result.Quantity} unitPrice={result.UnitPrice} " +
            $"earned={result.Earned} silver={result.SilverBalance}");
    }

    private async Task<bool> TryHandleCapitalNpcPageRequestAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (!CapitalNpcServiceProtocol.TryResolve(npc, out var service))
        {
            return false;
        }

        if (CapitalNpcServiceProtocol.IsShop(service))
        {
            await _session.SendAsync(
                PacketBuilder.CapitalNpcShopCatalog(
                    npc.InteractionId,
                    GetCapitalShopBalances(),
                    service),
                cancellationToken,
                "NpcShopCatalog",
                framed: false);
        }

        return true;
    }

    private async Task HandleCapitalNpcCreditExchangeAsync(
        uint npcId,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        if (dialogIndex != CapitalNpcServiceProtocol.ExchangeDialogIndex)
        {
            return;
        }

        if (CapitalNpcServiceProtocol.TryGetExchangePage(
                subId,
                out var pageSubIds))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    pageSubIds),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        if (await TryHandleReputationExchangeAsync(npcId, dialogIndex, subId, cancellationToken))
        {
            return;
        }

        // npc.reputation_exchange off: consume the confirmation without paying anything.
        if (subId is >= 311 and <= 316)
        {
            Console.WriteLine(
                $"[npc] credit exchange confirmation unavailable " +
                $"npc={npcId} subId={subId}");
        }
    }

    private async Task HandleCapitalNpcShopPurchaseAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null ||
            !CapitalNpcServiceProtocol.TryParsePurchase(
                packet.Payload,
                out var intent))
        {
            Console.Error.WriteLine(
                "[npc-shop] rejected malformed purchase " +
                $"length={packet.Length}");
            return;
        }

        // GM Mall (/mall): a purchase addressed to the NPC the GM Mall window is on.
        if (await TryHandleGmMallPurchaseAsync(intent, cancellationToken))
        {
            return;
        }

        // The function-key mall is opened by the client itself, so its purchases
        // carry no shop npc to price the item. They resolve against the captured
        // mall catalog and spend the mall's own currency instead.
        if (TryResolveMallPurchase(intent, out var mallOffer))
        {
            await CompleteMallPurchaseAsync(
                intent.ItemId,
                intent.Quantity,
                mallOffer,
                cancellationToken);
            return;
        }

        if (!TryResolveMapNpc(intent.NpcId, out var npc) ||
            !CapitalNpcServiceProtocol.TryResolve(npc, out var service) ||
            !CapitalNpcServiceProtocol.IsShop(service) ||
            !PacketBuilder.TryResolveCapitalNpcShopOffer(
                service,
                intent.Category,
                intent.ListingIndex,
                intent.ItemId,
                out var offer))
        {
            Console.Error.WriteLine(
                "[npc-shop] rejected malformed or untrusted purchase " +
                $"length={packet.Length}");
            return;
        }

        var result = await _capitalShopPurchases.PurchaseCapitalShopItemAsync(
            _account.Id,
            _character.Id,
            Guid.NewGuid(),
            offer,
            intent.Quantity,
            cancellationToken);
        if (!result.Purchased || result.Character is null)
        {
            Console.WriteLine(
                $"[npc-shop] purchase rejected character={_character.Name} " +
                $"npc={intent.NpcId} item={intent.ItemId} " +
                $"quantity={intent.Quantity} status={result.Status}");
            await SendCapitalShopCatalogAsync(
                npc,
                service,
                result.Status is
                    CapitalShopPurchaseStatus.InsufficientCurrency or
                    CapitalShopPurchaseStatus.InsufficientCapacity
                    ? GetCapitalShopBalances(
                        offer.Currency,
                        result.CurrencyBalance)
                    : GetCapitalShopBalances(),
                cancellationToken);
            return;
        }

        InstallCapitalShopProjection(result.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "NpcShopWalletStatus");
        await SendKitBagRefreshAsync(cancellationToken);
        await SendCapitalShopCatalogAsync(
            npc,
            service,
            GetCapitalShopBalances(),
            cancellationToken);
        Console.WriteLine(
            $"[npc-shop] purchased character={_character.Name} " +
            $"npc={intent.NpcId} item={intent.ItemId} " +
            $"quantity={intent.Quantity} unitPrice={offer.UnitPrice} " +
            $"currency={offer.Currency} " +
            $"balance={GetCapitalShopCurrencyBalance(offer.Currency)}");
    }

    /// <summary>
    /// Prices a purchase against the camp's captured mall catalog.
    /// </summary>
    /// <remarks>
    /// Only sessions that opened the mall may buy from it. The listing is addressed
    /// by category plus index exactly like a shop listing; the item id is checked
    /// against the record the index names, so a forged index cannot pick a cheaper
    /// price. The reference capture holds no mall purchase, so a request whose
    /// category or index does not line up is retried by item id and logged, and the
    /// price always comes from the captured record whichever path resolved it.
    /// </remarks>
    private bool TryResolveMallPurchase(
        CapitalNpcShopPurchaseIntent intent,
        out CapitalShopOffer offer)
    {
        offer = default;
        if (_character is null || !_mallCatalogOpen)
        {
            return false;
        }

        // A mall purchase normally carries no npc. When it does carry one it has to
        // be the mall npc; anything else is priced by its own shop, not by the mall.
        if (TryResolveMapNpc(intent.NpcId, out var npc) &&
            CapitalNpcServiceProtocol.TryResolve(npc, out var service) &&
            service != CapitalNpcServiceKind.Mall)
        {
            return false;
        }

        if (PacketBuilder.TryResolveMallCatalogOffer(
                _character.Camp,
                intent.Category,
                intent.ListingIndex,
                intent.ItemId,
                out offer,
                IsEditorAdminAccount))
        {
            return true;
        }

        if (PacketBuilder.TryResolveMallCatalogOfferByItem(
                _character.Camp,
                intent.ItemId,
                out offer))
        {
            Console.WriteLine(
                $"[mall] purchase resolved by item only character={_character.Name} " +
                $"npc={intent.NpcId} category={intent.Category} " +
                $"index={intent.ListingIndex} item={intent.ItemId}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Completes a mall purchase through the shared capital shop transaction and
    /// republishes only the wallet and the kit bag.
    /// </summary>
    /// <remarks>
    /// The catalog is deliberately not resent. The client renders the whole mall
    /// from the fifteen catalog frames, so replaying them after a purchase rebuilt
    /// the window: it jumped back to the first page and lost the selected listing,
    /// which is why a second Buy on the same item sent nothing until another item
    /// was selected. Republishing the catalog is what caused both symptoms, so the
    /// purchase answers with the wallet and bag the purchase actually changed.
    /// </remarks>
    private async Task CompleteMallPurchaseAsync(
        uint itemId,
        int quantity,
        CapitalShopOffer offer,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var result = await _capitalShopPurchases.PurchaseCapitalShopItemAsync(
            _account.Id,
            _character.Id,
            Guid.NewGuid(),
            offer,
            quantity,
            cancellationToken);
        if (!result.Purchased || result.Character is null)
        {
            Console.WriteLine(
                $"[mall] purchase rejected character={_character.Name} " +
                $"item={itemId} quantity={quantity} " +
                $"unitPrice={offer.UnitPrice} status={result.Status} " +
                $"balance={GetCapitalShopCurrencyBalance(offer.Currency)}");
            return;
        }

        InstallCapitalShopProjection(result.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "MallWalletStatus");
        await SendKitBagRefreshAsync(cancellationToken);
        await OnCarnivalMallPurchaseAsync(offer, quantity, cancellationToken); // W3 seasons carnival
        Console.WriteLine(
            $"[mall] purchased character={_character.Name} " +
            $"item={itemId} quantity={quantity} " +
            $"unitPrice={offer.UnitPrice} currency={offer.Currency} " +
            $"balance={GetCapitalShopCurrencyBalance(offer.Currency)}");
    }

    private void InstallCapitalShopProjection(GameCharacter updated)
    {
        if (_character is null ||
            updated.Id != _character.Id ||
            updated.AccountId != _character.AccountId ||
            updated.RealmId != _character.RealmId)
        {
            throw new InvalidDataException(
                "The purchased shop projection has the wrong owner.");
        }

        // A shop purchase changes only the wallet and kit bag. The legacy
        // character reload contains stored base HP/MP, not the live derived
        // equipment projection, so replacing the whole character would clamp
        // the authoritative live vitals to those base values.
        _character.Silver = updated.Silver;
        _character.Gold = updated.Gold;
        _character.BindingGold = updated.BindingGold;
        // The Point Exchanger charges the honor, point, and medal balances, so
        // the live character has to adopt them too or the session keeps quoting
        // the pre-purchase wallet to the client.
        _character.MedusaHonorPoints = updated.MedusaHonorPoints;
        _character.ExchangePoint = updated.ExchangePoint;
        _character.ExchangeMedal = updated.ExchangeMedal;
        _character.KitBag = updated.KitBag;
    }

    private int GetCapitalShopCurrencyBalance(
        CapitalNpcShopCurrency currency)
    {
        if (_character is null)
        {
            return 0;
        }
        return currency switch
        {
            CapitalNpcShopCurrency.Silver => _character.Silver,
            CapitalNpcShopCurrency.Gold => _character.Gold,
            CapitalNpcShopCurrency.BindingGold => _character.BindingGold,
            CapitalNpcShopCurrency.Honor => _character.MedusaHonorPoints,
            CapitalNpcShopCurrency.Point => _character.ExchangePoint,
            CapitalNpcShopCurrency.Medal => _character.ExchangeMedal,
            _ => 0
        };
    }

    private CapitalNpcShopBalances GetCapitalShopBalances(
        CapitalNpcShopCurrency? overrideCurrency = null,
        int overrideBalance = 0)
    {
        var balances = _character is null
            ? default
            : new CapitalNpcShopBalances(
                _character.Silver,
                _character.Gold,
                _character.BindingGold,
                _character.MedusaHonorPoints,
                _character.ExchangePoint,
                _character.ExchangeMedal);
        if (!overrideCurrency.HasValue)
        {
            return balances;
        }

        var balance = Math.Max(0, overrideBalance);
        return overrideCurrency.Value switch
        {
            CapitalNpcShopCurrency.Silver => balances with
            {
                Silver = balance
            },
            CapitalNpcShopCurrency.Gold => balances with
            {
                Gold = balance
            },
            CapitalNpcShopCurrency.BindingGold => balances with
            {
                BindingGold = balance
            },
            CapitalNpcShopCurrency.Honor => balances with
            {
                Honor = balance
            },
            CapitalNpcShopCurrency.Point => balances with
            {
                Point = balance
            },
            CapitalNpcShopCurrency.Medal => balances with
            {
                Medal = balance
            },
            _ => balances
        };
    }

    private Task SendCapitalShopCatalogAsync(
        NpcSpawnDefinition npc,
        CapitalNpcServiceKind service,
        CapitalNpcShopBalances balances,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.CapitalNpcShopCatalog(
                npc.InteractionId,
                balances,
                service),
            cancellationToken,
            "NpcShopCatalogRefresh",
            framed: false);
}
