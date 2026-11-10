using System.Buffers.Binary;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// TRADE: in-memory player trades and stalls. Nothing here moves an item or a coin: offers and
// stall goods only point at kit-bag rows (character_items.id), and the only writer is the one
// database transaction in PlayerTradeStore. A crash or disconnect therefore loses only the
// offer, never an item. Rules (IGG guide acid=46 and the 2008 server's Trade.cpp / Stall.h):
// request opens the window on both sides, each side adds items and money, any change clears
// both confirmations, the swap happens when both sides have confirmed after the last change.

internal sealed record PlayerTradeParty(
    int CharacterId,
    int AccountId,
    string Name,
    ClientSession Session,
    uint ObjectId,
    byte MapId,
    WorldInstanceId WorldInstanceId,
    long LifeRevision,
    int Level);

/// <summary>
/// One kit-bag row offered in a trade slot or a stall slot. <see cref="Fingerprint"/> is the row's
/// fingerprint at offer time (PlayerTradeStore.FingerprintSql); the commit refuses a row whose
/// fingerprint changed (review finding 2/6/17). <see cref="Item"/> is the database row as shown to
/// the partner or stall buyers.
/// </summary>
internal sealed record PlayerTradeOffer(
    int KitBagSlot,
    long InstanceId,
    uint ItemId,
    short Stack,
    string Fingerprint,
    CompactItemEntry Item);

internal enum PlayerTradePhase
{
    Open = 0,
    Committing = 1,
    Closed = 2
}

internal sealed class PlayerTradeSide
{
    public PlayerTradeSide(PlayerTradeParty party) => Party = party;

    public PlayerTradeParty Party { get; set; }

    public SortedDictionary<int, PlayerTradeOffer> Offers { get; } = [];

    public int Silver { get; set; }

    public int Gold { get; set; }

    public bool Locked { get; set; }

    /// <summary>Environment.TickCount64 of this side's last offer change (0 = none yet).</summary>
    public long LastChangeMs { get; set; }
}

internal sealed class PlayerTradeSession
{
    public PlayerTradeSession(PlayerTradeParty requester, PlayerTradeParty target)
    {
        Requester = new PlayerTradeSide(requester);
        Target = new PlayerTradeSide(target);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public PlayerTradeSide Requester { get; }

    public PlayerTradeSide Target { get; }

    public PlayerTradePhase Phase { get; set; } = PlayerTradePhase.Open;

    public PlayerTradeSide SideOf(int characterId) =>
        Requester.Party.CharacterId == characterId ? Requester : Target;

    public PlayerTradeSide PartnerOf(int characterId) =>
        Requester.Party.CharacterId == characterId ? Target : Requester;
}

internal sealed record PlayerStallGoods(
    long GoodsId,
    int Pocket,
    int StallIndex,
    PlayerTradeOffer Offer,
    int SilverPerUnit,
    int GoldPerUnit)
{
    public bool Reserved { get; init; }
}

internal sealed class PlayerStall
{
    public PlayerStall(PlayerTradeParty seller) => Seller = seller;

    public PlayerTradeParty Seller { get; set; }

    public bool Open { get; set; }

    public string Text { get; set; } = string.Empty;

    public Dictionary<(int Pocket, int Index), PlayerStallGoods> Goods { get; } = [];
}

internal enum PlayerTradeRefusal
{
    None = 0,
    Self,
    RequesterBusy,
    TargetBusy,
    NotTrading,
    NotOpen,
    BadSlot,
    SlotAlreadyOffered,
    StallFull,
    NoSuchGoods,
    GoodsReserved,
    OfferJustChanged,
    RequestCooldown
}

internal sealed class PlayerTradeBook
{
    public const int TradeSlots = 12;      // TradeUI controls 0x1c142..0x1c14d (0x4c23e0 refuses >= 12)
    public const int StallPockets = 3;     // StallSellUI pockets 0..2
    public const int StallSlotsPerPocket = 16; // 0x4bfff0 refuses index > 15

    // Generated rule (review finding 7): a confirmation that arrives this soon after the PARTNER
    // changed his offer may have been pressed while the old offer was on screen (last-second swap),
    // so it is refused and must be given again. Not in the guide or the 2008 server.
    public const long OfferChangeGuardMs = 1500;

    // Generated rule (review finding 8): after the target cancels (refuses) a trade, the same
    // requester cannot open a window on that target again for this long. Not in the guide.
    public const long DeclinedRequestCooldownMs = 10_000;

    public static PlayerTradeBook Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<int, PlayerTradeSession> _trades = [];
    private readonly Dictionary<int, PlayerStall> _stalls = [];
    private readonly Dictionary<(int Requester, int Target), long> _declined = [];
    private long _nextGoodsId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 8;

    public int TradeCount
    {
        get { lock (_gate) return _trades.Count / 2; }
    }

    public int StallCount
    {
        get { lock (_gate) return _stalls.Count; }
    }

    // ---- trades ---------------------------------------------------------------------------

    public PlayerTradeRefusal TryOpen(
        PlayerTradeParty requester,
        PlayerTradeParty target,
        out PlayerTradeSession? trade) =>
        TryOpen(requester, target, out trade, out _, Environment.TickCount64);

    /// <param name="droppedRequesterGoods">The requester's priced-but-not-shown goods that this
    /// request forgot (the requester chose to trade); the caller tells his client.</param>
    public PlayerTradeRefusal TryOpen(
        PlayerTradeParty requester,
        PlayerTradeParty target,
        out PlayerTradeSession? trade,
        out IReadOnlyList<PlayerStallGoods> droppedRequesterGoods,
        long nowMs)
    {
        trade = null;
        droppedRequesterGoods = [];
        if (requester.CharacterId == target.CharacterId) return PlayerTradeRefusal.Self;
        lock (_gate)
        {
            if (_trades.ContainsKey(requester.CharacterId) || HasOpenStallLocked(requester.CharacterId))
                return PlayerTradeRefusal.RequesterBusy;
            // Review finding 5/8/14: a target who has priced goods (stall window being filled, not
            // yet shown) is busy; a stranger's request must not wipe that setup.
            if (_trades.ContainsKey(target.CharacterId) || _stalls.ContainsKey(target.CharacterId))
                return PlayerTradeRefusal.TargetBusy;
            if (_declined.TryGetValue((requester.CharacterId, target.CharacterId), out var declinedAt) &&
                nowMs - declinedAt < DeclinedRequestCooldownMs)
            {
                return PlayerTradeRefusal.RequestCooldown;
            }
            _declined.Remove((requester.CharacterId, target.CharacterId));
            // The requester's own priced-but-not-shown goods are forgotten (he chose to trade; the
            // items never left the bag); the caller sends him 10063 for each.
            if (_stalls.Remove(requester.CharacterId, out var dropped))
                droppedRequesterGoods = dropped.Goods.Values.ToArray();
            trade = new PlayerTradeSession(requester, target);
            _trades[requester.CharacterId] = trade;
            _trades[target.CharacterId] = trade;
            return PlayerTradeRefusal.None;
        }
    }

    public PlayerTradeSession? Find(int characterId)
    {
        lock (_gate) return _trades.GetValueOrDefault(characterId);
    }

    /// <summary>Any change on either side clears both confirmations (2008 SetItem/SetMoney/RemoveItem).</summary>
    public PlayerTradeRefusal SetMoney(int characterId, int silver, int gold, out PlayerTradeSession? trade)
    {
        lock (_gate)
        {
            if (!TryOpenTradeLocked(characterId, out trade)) return PlayerTradeRefusal.NotOpen;
            var side = trade!.SideOf(characterId);
            side.Silver = silver;
            side.Gold = gold;
            side.LastChangeMs = Environment.TickCount64;
            ClearLocksLocked(trade);
            return PlayerTradeRefusal.None;
        }
    }

    public PlayerTradeRefusal AddItem(
        int characterId,
        int tradeSlot,
        PlayerTradeOffer offer,
        out PlayerTradeSession? trade,
        out PlayerTradeOffer? replaced)
    {
        replaced = null;
        lock (_gate)
        {
            if (!TryOpenTradeLocked(characterId, out trade)) return PlayerTradeRefusal.NotOpen;
            if (tradeSlot is < 0 or >= TradeSlots) return PlayerTradeRefusal.BadSlot;
            var side = trade!.SideOf(characterId);
            foreach (var (slot, existing) in side.Offers)
            {
                if (slot != tradeSlot && (existing.KitBagSlot == offer.KitBagSlot || existing.InstanceId == offer.InstanceId))
                    return PlayerTradeRefusal.SlotAlreadyOffered;
            }
            side.Offers.TryGetValue(tradeSlot, out replaced);
            side.Offers[tradeSlot] = offer;
            side.LastChangeMs = Environment.TickCount64;
            ClearLocksLocked(trade);
            return PlayerTradeRefusal.None;
        }
    }

    public PlayerTradeRefusal RemoveItem(
        int characterId,
        int tradeSlot,
        out PlayerTradeSession? trade)
    {
        lock (_gate)
        {
            if (!TryOpenTradeLocked(characterId, out trade)) return PlayerTradeRefusal.NotOpen;
            var side = trade!.SideOf(characterId);
            if (!side.Offers.Remove(tradeSlot)) return PlayerTradeRefusal.BadSlot;
            side.LastChangeMs = Environment.TickCount64;
            ClearLocksLocked(trade);
            return PlayerTradeRefusal.None;
        }
    }

    /// <summary>
    /// Confirms one side. Returns true in <paramref name="commit"/> for exactly one caller: the
    /// one whose confirmation completed the pair. That caller owns the database swap; the trade
    /// moves to <see cref="PlayerTradePhase.Committing"/> so no later change can slip in.
    /// </summary>
    public PlayerTradeRefusal Lock(int characterId, out PlayerTradeSession? trade, out bool commit) =>
        Lock(characterId, out trade, out commit, Environment.TickCount64);

    public PlayerTradeRefusal Lock(int characterId, out PlayerTradeSession? trade, out bool commit, long nowMs)
    {
        commit = false;
        lock (_gate)
        {
            if (!TryOpenTradeLocked(characterId, out trade)) return PlayerTradeRefusal.NotOpen;
            var partner = trade!.PartnerOf(characterId);
            if (partner.LastChangeMs != 0 && nowMs - partner.LastChangeMs < OfferChangeGuardMs)
                return PlayerTradeRefusal.OfferJustChanged;
            trade.SideOf(characterId).Locked = true;
            if (trade.Requester.Locked && trade.Target.Locked)
            {
                trade.Phase = PlayerTradePhase.Committing;
                commit = true;
            }
            return PlayerTradeRefusal.None;
        }
    }

    /// <summary>Removes the trade of this character (any phase except a running commit).</summary>
    public PlayerTradeSession? Cancel(int characterId, bool evenWhileCommitting = false) =>
        Cancel(characterId, evenWhileCommitting, byPlayer: false, Environment.TickCount64);

    /// <param name="byPlayer">The character pressed Cancel himself; when he is the target, the
    /// requester gets the <see cref="DeclinedRequestCooldownMs"/> cooldown on him.</param>
    public PlayerTradeSession? Cancel(int characterId, bool evenWhileCommitting, bool byPlayer, long nowMs)
    {
        lock (_gate)
        {
            if (!_trades.TryGetValue(characterId, out var trade)) return null;
            if (trade.Phase == PlayerTradePhase.Committing && !evenWhileCommitting) return null;
            RemoveLocked(trade);
            if (byPlayer && trade.Target.Party.CharacterId == characterId)
            {
                if (_declined.Count > 4096) _declined.Clear();
                _declined[(trade.Requester.Party.CharacterId, characterId)] = nowMs;
            }
            return trade;
        }
    }

    public void Finish(PlayerTradeSession trade)
    {
        lock (_gate) RemoveLocked(trade);
    }

    public IReadOnlyList<PlayerTradeSession> SnapshotTrades()
    {
        lock (_gate) return _trades.Values.Distinct().ToArray();
    }

    private bool HasOpenStallLocked(int characterId) =>
        _stalls.TryGetValue(characterId, out var stall) && (stall.Open || stall.Goods.Values.Any(static g => g.Reserved));

    private bool TryOpenTradeLocked(int characterId, out PlayerTradeSession? trade)
    {
        return _trades.TryGetValue(characterId, out trade) && trade.Phase == PlayerTradePhase.Open;
    }

    private static void ClearLocksLocked(PlayerTradeSession trade)
    {
        trade.Requester.Locked = false;
        trade.Target.Locked = false;
    }

    private void RemoveLocked(PlayerTradeSession trade)
    {
        trade.Phase = PlayerTradePhase.Closed;
        if (_trades.TryGetValue(trade.Requester.Party.CharacterId, out var a) && ReferenceEquals(a, trade))
            _trades.Remove(trade.Requester.Party.CharacterId);
        if (_trades.TryGetValue(trade.Target.Party.CharacterId, out var b) && ReferenceEquals(b, trade))
            _trades.Remove(trade.Target.Party.CharacterId);
    }

    // ---- stalls ---------------------------------------------------------------------------

    public bool IsTrading(int characterId)
    {
        lock (_gate) return _trades.ContainsKey(characterId);
    }

    public PlayerStall? FindStall(int characterId)
    {
        lock (_gate) return _stalls.GetValueOrDefault(characterId);
    }

    public PlayerTradeRefusal AddGoods(
        PlayerTradeParty seller,
        int pocket,
        int stallIndex,
        PlayerTradeOffer offer,
        int silverPerUnit,
        int goldPerUnit,
        out PlayerStallGoods? goods)
    {
        goods = null;
        if (pocket is < 0 or >= StallPockets || stallIndex is < 0 or >= StallSlotsPerPocket)
            return PlayerTradeRefusal.BadSlot;
        lock (_gate)
        {
            if (_trades.ContainsKey(seller.CharacterId)) return PlayerTradeRefusal.RequesterBusy;
            if (!_stalls.TryGetValue(seller.CharacterId, out var stall))
            {
                stall = new PlayerStall(seller);
                _stalls[seller.CharacterId] = stall;
            }
            stall.Seller = seller;
            foreach (var existing in stall.Goods.Values)
            {
                if ((existing.Pocket, existing.StallIndex) != (pocket, stallIndex) &&
                    (existing.Offer.KitBagSlot == offer.KitBagSlot || existing.Offer.InstanceId == offer.InstanceId))
                    return PlayerTradeRefusal.SlotAlreadyOffered;
            }
            if (stall.Goods.TryGetValue((pocket, stallIndex), out var occupied) && occupied.Reserved)
                return PlayerTradeRefusal.GoodsReserved;
            goods = new PlayerStallGoods(
                Interlocked.Increment(ref _nextGoodsId), pocket, stallIndex, offer, silverPerUnit, goldPerUnit);
            stall.Goods[(pocket, stallIndex)] = goods;
            return PlayerTradeRefusal.None;
        }
    }

    public PlayerTradeRefusal RemoveGoods(int sellerId, int pocket, int stallIndex)
    {
        lock (_gate)
        {
            if (!_stalls.TryGetValue(sellerId, out var stall)) return PlayerTradeRefusal.NoSuchGoods;
            if (!stall.Goods.TryGetValue((pocket, stallIndex), out var goods)) return PlayerTradeRefusal.NoSuchGoods;
            if (goods.Reserved) return PlayerTradeRefusal.GoodsReserved;
            stall.Goods.Remove((pocket, stallIndex));
            if (!stall.Open && stall.Goods.Count == 0) _stalls.Remove(sellerId);
            return PlayerTradeRefusal.None;
        }
    }

    public PlayerTradeRefusal OpenStall(PlayerTradeParty seller, string text, out PlayerStall? stall)
    {
        lock (_gate)
        {
            stall = null;
            if (_trades.ContainsKey(seller.CharacterId)) return PlayerTradeRefusal.RequesterBusy;
            if (!_stalls.TryGetValue(seller.CharacterId, out stall))
            {
                stall = new PlayerStall(seller);
                _stalls[seller.CharacterId] = stall;
            }
            stall.Seller = seller;
            stall.Text = text;
            stall.Open = true;
            return PlayerTradeRefusal.None;
        }
    }

    /// <summary>Closes and forgets the stall. Goods under a running purchase stay until it ends.</summary>
    public PlayerStall? CloseStall(int sellerId)
    {
        lock (_gate)
        {
            if (!_stalls.Remove(sellerId, out var stall)) return null;
            stall.Open = false;
            return stall;
        }
    }

    public IReadOnlyList<PlayerStallGoods> SnapshotGoods(int sellerId)
    {
        lock (_gate)
        {
            return _stalls.TryGetValue(sellerId, out var stall) && stall.Open
                ? stall.Goods.Values.OrderBy(static g => g.Pocket).ThenBy(static g => g.StallIndex).ToArray()
                : [];
        }
    }

    public IReadOnlyList<PlayerStall> SnapshotStalls()
    {
        lock (_gate) return _stalls.Values.ToArray();
    }

    /// <summary>Marks one open goods slot as being bought so a second buyer or a removal waits.</summary>
    public PlayerTradeRefusal ReserveGoods(int sellerId, long goodsId, int pocket, int stallIndex, out PlayerStallGoods? goods)
    {
        goods = null;
        lock (_gate)
        {
            if (!_stalls.TryGetValue(sellerId, out var stall) || !stall.Open) return PlayerTradeRefusal.NotOpen;
            if (!stall.Goods.TryGetValue((pocket, stallIndex), out var found) || found.GoodsId != goodsId)
                return PlayerTradeRefusal.NoSuchGoods;
            if (found.Reserved) return PlayerTradeRefusal.GoodsReserved;
            goods = found with { Reserved = true };
            stall.Goods[(pocket, stallIndex)] = goods;
            return PlayerTradeRefusal.None;
        }
    }

    /// <summary>
    /// Ends a reservation. <paramref name="soldQuantity"/> 0 releases it unchanged; a partial sale
    /// keeps the slot with the remaining stack; a full sale empties the slot.
    /// </summary>
    public void ReleaseGoods(int sellerId, PlayerStallGoods reserved, int soldQuantity)
    {
        lock (_gate)
        {
            if (!_stalls.TryGetValue(sellerId, out var stall) ||
                !stall.Goods.TryGetValue((reserved.Pocket, reserved.StallIndex), out var current) ||
                current.GoodsId != reserved.GoodsId)
            {
                return;
            }
            var remaining = current.Offer.Stack - soldQuantity;
            if (soldQuantity <= 0)
            {
                stall.Goods[(reserved.Pocket, reserved.StallIndex)] = current with { Reserved = false };
            }
            else if (remaining > 0)
            {
                stall.Goods[(reserved.Pocket, reserved.StallIndex)] = current with
                {
                    Reserved = false,
                    Offer = current.Offer with
                    {
                        Stack = checked((short)remaining),
                        Item = current.Offer.Item with { Stack = checked((short)remaining) }
                    }
                };
            }
            else
            {
                stall.Goods.Remove((reserved.Pocket, reserved.StallIndex));
            }
        }
    }

    /// <summary>Drops goods that point at a kit-bag row the seller no longer has in that slot.</summary>
    public int DropGoodsNotIn(int sellerId, IReadOnlySet<long> liveInstanceIds)
    {
        lock (_gate)
        {
            if (!_stalls.TryGetValue(sellerId, out var stall)) return 0;
            var stale = stall.Goods
                .Where(pair => !pair.Value.Reserved && !liveInstanceIds.Contains(pair.Value.Offer.InstanceId))
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (var key in stale) stall.Goods.Remove(key);
            return stale.Length;
        }
    }

    internal void ResetForChecks()
    {
        lock (_gate)
        {
            _trades.Clear();
            _stalls.Clear();
            _declined.Clear();
        }
    }
}

/// <summary>Client-to-server frame readers. Offsets are wire offsets (0 = u16 length).</summary>
internal static class PlayerTradeRequests
{
    public readonly record struct TradeRequest(uint SelfObjectId, uint TargetObjectId, int State, string TargetName);

    public readonly record struct TradeAddItem(int TradeSlot, int BagPage, int BagGrid);

    public readonly record struct StallState(bool Open, string Text);

    public readonly record struct StallAddItem(int StallIndex, int Pocket, int BagPage, int BagGrid, int Silver, int Gold);

    public readonly record struct StallBrowse(uint SellerObjectId, string SellerName);

    public readonly record struct StallBuy(uint SellerObjectId, string SellerName, long GoodsId, int Pocket, int StallIndex, int Quantity);

    // 10104 from TargetInfo 0x62a34f (State 1), TradeUI 0x633d66 (2, the OK button) and
    // 0x63412f / 0x6344bd / 0x658333 (4: Cancel, window close, stall start).
    public static bool TryReadTrade(ReadOnlySpan<byte> frame, out TradeRequest request)
    {
        request = default;
        if (frame.Length < 84) return false;
        request = new TradeRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(frame[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[12..]),
            PacketText.ReadFixedAscii(frame, 84, Math.Min(64, frame.Length - 84)));
        return true;
    }

    // 10105 from TradeUI 0x6343e4: +8 silver, +12 gold, both absolute amounts.
    public static bool TryReadMoney(ReadOnlySpan<byte> frame, out int silver, out int gold)
    {
        silver = gold = 0;
        if (frame.Length < 16) return false;
        silver = BinaryPrimitives.ReadInt32LittleEndian(frame[8..]);
        gold = BinaryPrimitives.ReadInt32LittleEndian(frame[12..]);
        return true;
    }

    // 10107 from TradeUI 0x63404d: +8 trade slot, +12 bag page (HandUI+0x18), +16 grid (HandUI+0x1c).
    public static bool TryReadAddItem(ReadOnlySpan<byte> frame, out TradeAddItem request)
    {
        request = default;
        if (frame.Length < 20) return false;
        request = new TradeAddItem(
            BinaryPrimitives.ReadInt32LittleEndian(frame[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[16..]));
        return true;
    }

    // 10108 from TradeUI 0x634291: +8 trade slot.
    public static bool TryReadRemoveItem(ReadOnlySpan<byte> frame, out int tradeSlot)
    {
        tradeSlot = -1;
        if (frame.Length < 12) return false;
        tradeSlot = BinaryPrimitives.ReadInt32LittleEndian(frame[8..]);
        return true;
    }

    // 10061 from StallSellUI 0x61d546 (open, with the sign text) and 0x61d71f / CPlayer 0x4a6f7d (close).
    public static bool TryReadStall(ReadOnlySpan<byte> frame, out StallState request)
    {
        request = default;
        if (frame.Length < 9) return false;
        var chars = new List<char>(PacketBuilder.PlayerStallTextChars);
        for (var offset = 10; offset + 2 <= Math.Min(frame.Length, 10 + PacketBuilder.PlayerStallTextChars * 2); offset += 2)
        {
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
            if (c == '\0') break;
            chars.Add(c);
        }
        request = new StallState(frame[8] != 0, new string(chars.ToArray()));
        return true;
    }

    // 10062 from StallCounterUI 0x61c97d / 0x61de26: +4 index, +8 pocket, +12 page, +16 grid,
    // +20 silver per unit, +24 gold per unit, +32 int64 -1.
    public static bool TryReadStallAddItem(ReadOnlySpan<byte> frame, out StallAddItem request)
    {
        request = default;
        if (frame.Length < 28) return false;
        request = new StallAddItem(
            BinaryPrimitives.ReadInt32LittleEndian(frame[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[24..]));
        return true;
    }

    // 10063 from 0x61def5: +4 index, +8 pocket.
    public static bool TryReadStallDeleteItem(ReadOnlySpan<byte> frame, out int stallIndex, out int pocket)
    {
        stallIndex = pocket = -1;
        if (frame.Length < 12) return false;
        stallIndex = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
        pocket = BinaryPrimitives.ReadInt32LittleEndian(frame[8..]);
        return true;
    }

    // 10065 from CMainModule 0x47e22a (clicking a player whose stall flag is set): +16 seller
    // name, +48 seller id, the rest zero.
    public static bool TryReadStallBrowse(ReadOnlySpan<byte> frame, out StallBrowse request)
    {
        request = default;
        if (frame.Length < 52) return false;
        request = new StallBrowse(
            BinaryPrimitives.ReadUInt32LittleEndian(frame[48..]),
            PacketText.ReadFixedAscii(frame, 16, 32));
        return true;
    }

    // 10066 from StallBuyUI 0x61b06d: +4 seller, +8 buyer, +12 seller name, +44 buyer name,
    // +80 int64 goods id, +88 pocket, +92 index, +96 quantity.
    public static bool TryReadStallBuy(ReadOnlySpan<byte> frame, out StallBuy request)
    {
        request = default;
        if (frame.Length < 100) return false;
        request = new StallBuy(
            BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]),
            PacketText.ReadFixedAscii(frame, 12, 32),
            BinaryPrimitives.ReadInt64LittleEndian(frame[80..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[88..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[92..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[96..]));
        return true;
    }
}
