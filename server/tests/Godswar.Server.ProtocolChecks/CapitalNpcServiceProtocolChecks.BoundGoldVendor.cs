using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CapitalNpcServiceProtocolChecks
{
    private static void CheckReviewedBoundGoldVendorStock()
    {
        var frames = ReadCatalogFrames(PacketBuilder.CapitalNpcShopCatalog(
            5084, new CapitalNpcShopBalances(11, 22, 33),
            CapitalNpcServiceKind.BoundGoldVendor));
        var stock = Enumerable.Range(0, 4).Select(category => frames
            .Where(frame => frame[8] == category)
            .SelectMany(ReadItemIds).ToArray()).ToArray();
        Check.True(stock.Select(ids => ids.Length).SequenceEqual(
            new[] { 33, 31, 35, 20 }), "reviewed vendor tab sizes fit native 45-slot tabs");
        CheckVendorClientItemLoadability(stock.SelectMany(ids => ids).Distinct().ToArray());
        CheckHolySuitWareQuantityPresets(frames, stock[3]);
        Check.True(stock[2].Take(3).SequenceEqual(new uint[] { 9030, 9031, 9032 }),
            "Heated, Cooled and Zephyr Holy Stones appear together first in H.Ward");
        Check.True(stock[2].TakeLast(4).SequenceEqual(new uint[] { 4270, 4271, 4272, 4273 }),
            "Socket Spells I through IV are the final four H.Ward listings");
        Check.True(stock[2].Take(31).SequenceEqual(new uint[] {
            9030,9031,9032,9040,9041,9042,9050,9051,9052,9053,9054,
            9060,9061,9062,9063,9064,9065,9066,9067,9080,9081,9082,9083,9084,9085,9086,9087,
            9090,9091,9092,9093 }), "every other H.Ward item retains its prior relative order");
        byte[] captionKeys = [44, 26, 30, 45];
        foreach (var frame in frames)
        {
            for (var index = 0; index < frame[10]; index++)
            {
                Check.Equal(captionKeys[frame[8]], frame[16 + index * 88 + 64],
                    "vendor records select scoped Gears/Pet/H.Ward/Holy Suit captions");
            }
        }
        Check.True(stock[2].All(id => id is not (4011 or 4262 or 4263 or
            4264 or 4265 or 4302 or 4306 or 4324 or 4325 or 4326 or 4327)),
            "third tab removes potions, war materials, and portal scrolls");
        Check.True(stock[3].All(id => id is not (4010 or 4041 or
            >= 9060 and <= 9067 or >= 9080 and <= 9087)),
            "Holy Suit tab removes elemental spirits and potions");

        (int Category, uint Id, int Price)[] additions =
        [
            (0, 3200, 5_000), (1, 11005, 5_000), (2, 9032, 460),
            (2, 9090, 230), (2, 9091, 230), (2, 9092, 230), (2, 9093, 230),
            (2, 4270, 23_000), (2, 4271, 98_000), (2, 4272, 392_000), (2, 4273, 1_568_000),
            (3, 9025, 50_000)
        ];
        foreach (var addition in additions)
        {
            var index = Array.IndexOf(stock[addition.Category], addition.Id);
            Check.True(index >= 0 && PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.BoundGoldVendor, addition.Category,
                index, addition.Id, out var offer) &&
                offer.UnitPrice == addition.Price &&
                offer.Currency == CapitalNpcShopCurrency.BindingGold &&
                offer.Item.Stack == 1 && offer.Item.SocketCount == 0,
                $"vendor addition {addition.Id} resolves exact unit price and B-Gold");
        }
    }

    private static void CheckHolySuitWareQuantityPresets(IReadOnlyList<byte[]> frames, uint[] ids)
    {
        Check.True(ids.SequenceEqual(new uint[] { 9010,9011,9012,9013,9014,9015,9016,9017,
            3932,9050,9010,9011,9012,9013,9014,9015,9016,9017,9024,9025 }),
            "Holy Suit offers all eight single wares, retained middle stock, all eight bulk presets, then Holy Box V and Ascension Core");
        var records = frames.Where(frame => frame[8] == 3).SelectMany(frame =>
            Enumerable.Range(0, frame[10]).Select(index => frame.AsSpan(16 + index * 88, 88).ToArray())).ToArray();
        int[] prices = [23,115,575,1152,3152,8954,15673,31346];
        for (var tier = 0; tier < 8; tier++)
        {
            foreach (var index in new[] { tier, tier + 10 })
            {
                Check.Equal(index < 8 ? (byte)1 : (byte)25, records[index][27],
                    "single and bulk listings keep separate native quantity presets");
                Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(CapitalNpcServiceKind.BoundGoldVendor,
                    3, index, checked((uint)(9010 + tier)), out var offer) && offer.UnitPrice == prices[tier] &&
                    offer.Item.Stack == 1 && offer.Currency == CapitalNpcShopCurrency.BindingGold,
                    "both presets retain the same per-unit price; the request owns purchased quantity");
            }
        }
        Check.Equal((byte)1, records[^1][27], "Ascension Core defaults to a single-piece purchase");
    }
}
