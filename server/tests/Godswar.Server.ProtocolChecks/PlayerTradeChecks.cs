using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Trade;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// TRADE: frame layouts read from Origin.exe 2.51 (Development/build/trade/README.md), the trade and
// stall book's rules, and the gate (off by default, so 10104-10108 / 10061-10066 still reach the
// dispatcher's default branch).
internal static class PlayerTradeChecks
{
    public const string CheckName = "Player trade and stall frames, book rules and gate";

    public static Task RunAsync()
    {
        CheckFrames();
        CheckRequestReaders();
        CheckTradeBook();
        CheckStallBook();
        CheckReviewFixes();
        CheckFreeSlots();
        CheckGate();
        return Task.CompletedTask;
    }

    private static void CheckFrames()
    {
        var trade = PacketBuilder.PlayerTrade(0x1448, "Alice", 77, "Bob", 2, 1);
        Header(trade, 148, 10104);
        Check.Equal(0x1448u, U32(trade, 4), "10104 +4 uiID");
        Check.Equal(77u, U32(trade, 8), "10104 +8 uiObjectID");
        Check.Equal(2, I32(trade, 12), "10104 +12 State");
        Check.Equal(1, I32(trade, 16), "10104 +16 TraderState");
        Check.Equal("Alice", Ascii(trade, 20, 64), "10104 +20 chName");
        Check.Equal("Bob", Ascii(trade, 84, 64), "10104 +84 chObjectName");

        var money = PacketBuilder.PlayerTradeMoney(77, "Bob", 1234, 5);
        Header(money, 80, 10105);
        Check.True(U32(money, 4) == 77 && I32(money, 8) == 1234 && I32(money, 12) == 5 && Ascii(money, 16, 64) == "Bob",
            "10105 +4 id, +8 silver, +12 gold, +16 name");

        var item = CompactItemEntry.Empty with { Id = 4001, Quality = 1, Grade = 1, Stack = 7 };
        var offered = PacketBuilder.PlayerTradeItem(77, "Bob", 11, item);
        Header(offered, 120, 10106);
        Check.True(U32(offered, 4) == 77 && I32(offered, 8) == 11 && U32(offered, 12) == 4001 && Ascii(offered, 16, 32) == "Bob",
            "10106 +4 id, +8 slot, +12 base id, +16 name");
        Check.Equal(4001u, U32(offered, 48), "10106 +48 item record starts with the item id");
        Check.Equal((byte)7, offered[48 + 27], "10106 item record stack byte");
        Check.Throws<ArgumentOutOfRangeException>(() => PacketBuilder.PlayerTradeItem(1, "x", 12, item), "trade slot 12 refused");

        var removed = PacketBuilder.PlayerTradeRemoveItem(77, "Bob", 3);
        Header(removed, 76, 10108);
        Check.True(U32(removed, 4) == 77 && I32(removed, 8) == 3 && Ascii(removed, 12, 64) == "Bob", "10108 +4 id, +8 slot, +12 name");

        var stall = PacketBuilder.PlayerStall(77, true, "Cheap pots", "Bob");
        Header(stall, 108, 10061);
        Check.True(U32(stall, 4) == 77 && stall[8] == 1, "10061 +4 id, +8 state");
        Check.Equal("Cheap pots", Encoding.Unicode.GetString(stall, 10, 20), "10061 +10 wide text");
        Check.Equal("Bob", Ascii(stall, 74, 32), "10061 +74 seller name");

        var add = PacketBuilder.PlayerStallAddItem(15, 2, 3, 23, 100, 2, 0x1122334455L);
        Header(add, 40, 10062);
        Check.True(I32(add, 4) == 15 && I32(add, 8) == 2 && I32(add, 12) == 3 && I32(add, 16) == 23 &&
            I32(add, 20) == 100 && I32(add, 24) == 2 && BinaryPrimitives.ReadInt64LittleEndian(add.AsSpan(32)) == 0x1122334455L,
            "10062 +4 index, +8 pocket, +12 page, +16 grid, +20 silver, +24 gold, +32 goods id");

        Header(PacketBuilder.PlayerStallDeleteItem(4, 1), 12, 10063);

        var shown = PacketBuilder.PlayerStallItem(77, "Bob", 99, 2, 15, 300, 0, item);
        Header(shown, 136, 10065);
        Check.True(BinaryPrimitives.ReadInt64LittleEndian(shown.AsSpan(8)) == 99 && U32(shown, 48) == 77 &&
            shown[52] == 2 && shown[53] == 15 && I32(shown, 56) == 300 && I32(shown, 60) == 0 && U32(shown, 64) == 4001,
            "10065 +8 goods id, +48 seller, +52 pocket, +53 index, +56 silver, +60 gold, +64 item");

        var bought = PacketBuilder.PlayerStallBuyItem(77, "Bob", 0x1448, "Alice", 99, 2, 15, 3);
        Header(bought, 104, 10066);
        Check.True(U32(bought, 4) == 77 && U32(bought, 8) == 0x1448 && Ascii(bought, 12, 32) == "Bob" &&
            Ascii(bought, 44, 32) == "Alice" && BinaryPrimitives.ReadInt64LittleEndian(bought.AsSpan(80)) == 99 &&
            I32(bought, 88) == 2 && I32(bought, 92) == 15 && I32(bought, 96) == 3,
            "10066 +4 seller, +8 buyer, names, +80 goods id, +88 pocket, +92 index, +96 quantity");

        Check.Equal(0x1448u, PacketBuilder.TradeObjectIdFor(77, true), "the recipient sees himself as 0x1448");
        Check.Equal(77u, PacketBuilder.TradeObjectIdFor(77, false), "and others by object id");
    }

    private static void CheckRequestReaders()
    {
        // Built the way TargetInfo 0x62a34f fills the 148-byte request.
        var request = new byte[148];
        Put(request, 0, 148, 10104);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 0x1448);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 77);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(16), 1);
        Encoding.ASCII.GetBytes("Bob").CopyTo(request.AsSpan(84));
        Check.True(PlayerTradeRequests.TryReadTrade(request, out var trade) &&
            trade.TargetObjectId == 77 && trade.State == 1 && trade.TargetName == "Bob", "10104 request reader");

        var money = new byte[80];
        BinaryPrimitives.WriteInt32LittleEndian(money.AsSpan(8), 500);
        BinaryPrimitives.WriteInt32LittleEndian(money.AsSpan(12), 3);
        Check.True(PlayerTradeRequests.TryReadMoney(money, out var silver, out var gold) && silver == 500 && gold == 3, "10105 reader");

        var add = new byte[84];
        BinaryPrimitives.WriteInt32LittleEndian(add.AsSpan(8), 5);
        BinaryPrimitives.WriteInt32LittleEndian(add.AsSpan(12), 2);
        BinaryPrimitives.WriteInt32LittleEndian(add.AsSpan(16), 9);
        Check.True(PlayerTradeRequests.TryReadAddItem(add, out var addItem) && addItem == new PlayerTradeRequests.TradeAddItem(5, 2, 9),
            "10107 reader: +8 slot, +12 page, +16 grid");

        var stall = PacketBuilder.PlayerStall(0x1448, true, "Sale", "Me");
        Check.True(PlayerTradeRequests.TryReadStall(stall, out var stallState) && stallState.Open && stallState.Text == "Sale", "10061 reader");

        var stallAdd = PacketBuilder.PlayerStallAddItem(15, 2, 3, 23, 100, 2, -1);
        Check.True(PlayerTradeRequests.TryReadStallAddItem(stallAdd, out var stallItem) &&
            stallItem == new PlayerTradeRequests.StallAddItem(15, 2, 3, 23, 100, 2), "10062 reader");

        var browse = new byte[136];
        Encoding.ASCII.GetBytes("Bob").CopyTo(browse.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(browse.AsSpan(48), 77);
        Check.True(PlayerTradeRequests.TryReadStallBrowse(browse, out var browsed) && browsed.SellerObjectId == 77 && browsed.SellerName == "Bob",
            "10065 reader: +16 name, +48 seller");

        var buy = PacketBuilder.PlayerStallBuyItem(77, "Bob", 0x1448, "Me", 99, 1, 4, 2);
        Check.True(PlayerTradeRequests.TryReadStallBuy(buy, out var buyRequest) && buyRequest.SellerObjectId == 77 &&
            buyRequest.GoodsId == 99 && buyRequest.Pocket == 1 && buyRequest.StallIndex == 4 && buyRequest.Quantity == 2, "10066 reader");
    }

    private static void CheckTradeBook()
    {
        var book = new PlayerTradeBook();
        var a = Party(1, "A");
        var b = Party(2, "B");
        var c = Party(3, "C");
        Same(PlayerTradeRefusal.Self, book.TryOpen(a, a, out _), "no trade with yourself");
        Same(PlayerTradeRefusal.None, book.TryOpen(a, b, out var trade), "open");
        Same(PlayerTradeRefusal.TargetBusy, book.TryOpen(c, b, out _), "the target is busy");
        Same(PlayerTradeRefusal.RequesterBusy, book.TryOpen(a, c, out _), "the requester is busy");

        var offer = Offer(10, 500, 4001, 1);
        Same(PlayerTradeRefusal.None, book.AddItem(1, 0, offer, out _, out _), "add an item");
        Same(PlayerTradeRefusal.SlotAlreadyOffered, book.AddItem(1, 1, offer, out _, out _), "the same bag row twice is refused");
        Same(PlayerTradeRefusal.BadSlot, book.AddItem(1, 12, Offer(11, 501, 4001, 1), out _, out _), "trade slot 12 is refused");

        Same(PlayerTradeRefusal.None, book.Lock(1, out _, out var commit), "A confirms");
        Check.True(!commit, "one confirmation does not commit");
        Same(PlayerTradeRefusal.None, book.SetMoney(2, 100, 0, out _), "B changes money");
        Check.True(!trade!.Requester.Locked && !trade.Target.Locked, "a change clears both confirmations");
        // Both confirmations are given after the 1.5 s swap guard (A's AddItem above was "just now").
        var later = Environment.TickCount64 + PlayerTradeBook.OfferChangeGuardMs + 1;
        Same(PlayerTradeRefusal.None, book.Lock(2, out _, out commit, later), "B confirms after the guard");
        Check.True(!commit, "B alone does not commit");
        book.Lock(1, out _, out commit, later);
        Check.True(commit && trade.Phase == PlayerTradePhase.Committing, "the second confirmation commits once");
        Same(PlayerTradeRefusal.NotOpen, book.SetMoney(1, 5, 0, out _), "no change while committing");
        Check.True(book.Cancel(2) is null, "cancel waits for a running commit");
        book.Finish(trade);
        Check.True(book.Find(1) is null && book.Find(2) is null, "finish removes both sides");

        Same(PlayerTradeRefusal.None, book.TryOpen(a, b, out var second), "open again");
        Check.True(ReferenceEquals(book.Cancel(2), second) && book.Find(1) is null, "cancel by either side ends it");
    }

    private static void CheckStallBook()
    {
        var book = new PlayerTradeBook();
        var seller = Party(5, "Seller");
        var buyer = Party(6, "Buyer");
        Same(PlayerTradeRefusal.None, book.AddGoods(seller, 0, 0, Offer(1, 900, 4001, 10), 50, 0, out var goods), "price an item");
        Same(PlayerTradeRefusal.TargetBusy, book.TryOpen(buyer, seller, out _), "a stranger cannot open a trade on a player pricing goods");
        Check.True(book.FindStall(5) is { Goods.Count: 1 }, "and the priced goods survive that request");
        Same(PlayerTradeRefusal.None, book.TryOpen(seller, buyer, out var early, out var dropped, Environment.TickCount64),
            "the keeper's own request: goods priced but not on show do not block it");
        Check.True(book.FindStall(5) is null && dropped.Count == 1, "and are forgotten (returned for 10063) when he opens a trade");
        book.Cancel(5);
        Same(PlayerTradeRefusal.None, book.AddGoods(seller, 0, 0, Offer(1, 900, 4001, 10), 50, 0, out goods), "price it again");
        Same(PlayerTradeRefusal.SlotAlreadyOffered, book.AddGoods(seller, 0, 1, Offer(1, 900, 4001, 10), 50, 0, out _), "the same bag row twice");
        Same(PlayerTradeRefusal.BadSlot, book.AddGoods(seller, 3, 0, Offer(2, 901, 4001, 1), 50, 0, out _), "pocket 3 refused");
        Check.Equal(0, book.SnapshotGoods(5).Count, "a closed stall shows nothing");
        book.OpenStall(seller, "Sale", out _);
        Check.Equal(1, book.SnapshotGoods(5).Count, "an open stall shows its goods");
        Same(PlayerTradeRefusal.RequesterBusy, book.TryOpen(seller, buyer, out _), "a stall keeper cannot trade");

        Same(PlayerTradeRefusal.None, book.ReserveGoods(5, goods!.GoodsId, 0, 0, out var reserved), "reserve for a buyer");
        Same(PlayerTradeRefusal.GoodsReserved, book.ReserveGoods(5, goods.GoodsId, 0, 0, out _), "a second buyer waits");
        Same(PlayerTradeRefusal.GoodsReserved, book.RemoveGoods(5, 0, 0), "the seller cannot pull it mid-sale");
        book.ReleaseGoods(5, reserved!, 4);
        var left = book.SnapshotGoods(5).Single();
        Check.True(left.Offer.Stack == 6 && left.Offer.Item.Stack == 6 && !left.Reserved, "a partial sale leaves the rest on show");
        book.ReserveGoods(5, goods.GoodsId, 0, 0, out reserved);
        book.ReleaseGoods(5, reserved!, 6);
        Check.Equal(0, book.SnapshotGoods(5).Count, "a full sale empties the slot");
        Same(PlayerTradeRefusal.NoSuchGoods, book.ReserveGoods(5, goods.GoodsId, 0, 0, out _), "sold goods cannot be bought again");

        book.AddGoods(seller, 1, 2, Offer(3, 902, 4002, 1), 10, 0, out _);
        Check.Equal(1, book.DropGoodsNotIn(5, new HashSet<long>()), "goods whose bag row is gone are dropped");
        Check.True(book.CloseStall(5) is not null && book.FindStall(5) is null, "closing forgets the stall");
    }

    // Review fixes: last-second swap guard, declined-request cooldown, offer fingerprint carried.
    private static void CheckReviewFixes()
    {
        var book = new PlayerTradeBook();
        var a = Party(21, "A");
        var b = Party(22, "B");
        var now = 1_000_000L;
        Same(PlayerTradeRefusal.None, book.TryOpen(a, b, out var trade, out _, now), "open");
        Same(PlayerTradeRefusal.None, book.Lock(22, out _, out var commit, now), "B confirms before any change");
        Same(PlayerTradeRefusal.None, book.AddItem(21, 0, Offer(1, 700, 4001, 1), out _, out _), "A swaps the offer");
        Check.True(!trade!.Target.Locked, "the swap clears B's confirmation");
        var changed = trade.Requester.LastChangeMs;
        Same(PlayerTradeRefusal.OfferJustChanged, book.Lock(22, out _, out commit, changed + 100),
            "B's OK arriving 100 ms after A's change is refused (it may be for the old offer)");
        Check.True(!trade.Target.Locked && !commit, "and does not count");
        Same(PlayerTradeRefusal.None, book.Lock(21, out _, out commit, changed + 100), "A may confirm right after his own change");
        Check.True(!commit, "A alone does not commit");
        Same(PlayerTradeRefusal.None, book.Lock(22, out _, out commit, changed + PlayerTradeBook.OfferChangeGuardMs),
            "B's OK after the guard counts");
        Check.True(commit, "and completes the pair");
        book.Finish(trade);

        Same(PlayerTradeRefusal.None, book.TryOpen(a, b, out var second, out _, now), "open again");
        Check.True(ReferenceEquals(book.Cancel(22, false, byPlayer: true, now), second), "B refuses with Cancel");
        Same(PlayerTradeRefusal.RequestCooldown, book.TryOpen(a, b, out _, out _, now + 1000), "A cannot force the window on B again at once");
        Same(PlayerTradeRefusal.None, book.TryOpen(b, a, out var third, out _, now + 1000), "B may still ask A");
        book.Cancel(21, false, byPlayer: true, now + 1000);
        Same(PlayerTradeRefusal.None, book.TryOpen(a, b, out var fourth, out _, now + PlayerTradeBook.DeclinedRequestCooldownMs + 1),
            "A may ask B again after the cooldown");
        book.Finish(fourth!);
        Check.True(third is not null, "third trade opened");
        Check.True(Offer(1, 700, 4001, 1).Fingerprint == "fp-700", "an offer carries the row fingerprint the commit re-checks");
        Check.True(GameClientHandler.PlayerTradeRefusedItemIds.Contains(10080), "free Lelantine Farm wooden tools cannot be traded (generated)");
    }

    private static void CheckFreeSlots()
    {
        var occupied = Enumerable.Range(0, 96).ToHashSet();
        Check.Equal(0, PlayerTradeStore.FreeSlots(occupied, []).Count, "a full bag has no free slot");
        Check.Equal(2, PlayerTradeStore.FreeSlots(occupied, [4, 9]).Count, "slots given away count as free");
        occupied.Remove(30);
        Check.Equal(30, PlayerTradeStore.FreeSlots(occupied, []).Single(), "lowest free slot");
    }

    private static void CheckGate()
    {
        ServerRates.ResetToDefaults();
        Check.True(ServerRateCatalog.TryGet(ServerRateKeys.PlayerTrade, out var tradeRate) && tradeRate.DefaultValue == 0m,
            "trade.player exists and defaults to 0");
        Check.True(ServerRateCatalog.TryGet(ServerRateKeys.PlayerStalls, out var stallRate) && stallRate.DefaultValue == 0m,
            "trade.stalls exists and defaults to 0");
        foreach (var opcode in new ushort[] { 10061, 10062, 10063, 10065, 10066, 10104, 10105, 10106, 10107, 10108 })
        {
            Check.True(!GameClientHandler.ClaimsPlayerTradePacket(opcode), $"gate off: {opcode} still reaches the default branch");
        }
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.PlayerTrade, 1m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        Check.True(GameClientHandler.ClaimsPlayerTradePacket(10104) && GameClientHandler.ClaimsPlayerTradePacket(10107),
            "trade.player 1 claims the trade opcodes");
        Check.True(!GameClientHandler.ClaimsPlayerTradePacket(10061) && !GameClientHandler.ClaimsPlayerTradePacket(10106),
            "but not the stall opcodes, nor 10106 which the client never sends");
        Check.True(!GameClientHandler.ClaimsPlayerTradePacket(10060) && !GameClientHandler.ClaimsPlayerTradePacket(10109),
            "neighbouring opcodes are untouched");
        ServerRates.ResetToDefaults();
    }

    private static void Same(PlayerTradeRefusal expected, PlayerTradeRefusal actual, string description) =>
        Check.True(expected == actual, $"{description} (got {actual})");

    private static PlayerTradeParty Party(int id, string name) =>
        new(id, id, name, null!, (uint)(1000 + id), 1, default(WorldInstanceId), 0, 100);

    private static PlayerTradeOffer Offer(int slot, long instance, uint itemId, short stack) =>
        new(slot, instance, itemId, stack, $"fp-{instance}", CompactItemEntry.Empty with { Id = itemId, Quality = 1, Grade = 1, Stack = stack });

    private static void Header(byte[] frame, int length, ushort opcode)
    {
        Check.Equal(length, frame.Length, $"{opcode} length");
        Check.Equal((ushort)length, BinaryPrimitives.ReadUInt16LittleEndian(frame), $"{opcode} size field");
        Check.Equal(opcode, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2)), $"{opcode} opcode field");
    }

    private static void Put(byte[] frame, int offset, ushort length, ushort opcode)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(offset), length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(offset + 2), opcode);
    }

    private static uint U32(byte[] frame, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset));

    private static int I32(byte[] frame, int offset) => BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(offset));

    private static string Ascii(byte[] frame, int offset, int length) => PacketText.ReadFixedAscii(frame, offset, length);
}
