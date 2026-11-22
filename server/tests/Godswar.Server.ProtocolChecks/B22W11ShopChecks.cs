using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// B22 W11 (batch 2026-11-22): d58 Athens Skill Vendor / Props shop charge silver like Sparta, d29 Point Exchanger rows
// that charged B-Gold now charge honour.
internal static class B22W11ShopChecks
{
    public const string CheckName = "B22 W11: Athens skill/props shelves in silver, Point Exchanger B-Gold rows in honour";

    private static readonly uint[] HonourIds =
    [
        9970, 9971, 9972, 9973, 9974, 9975, 9976, 9977, 9978, 9979, 9980, 9981, 9982, 9983, 9984, 9985,
        9025, 9032, 9090, 9091, 9092, 9093, 9068, 9069, 9088, 9089, 4272, 4273,
        16300, 16303, 16306, 16309, 16312, 16315, 16318, 14029, 14030
    ];

    public static Task RunAsync()
    {
        Check.Equal(37, HonourIds.Distinct().Count(), "37 distinct ids");
        foreach (var id in HonourIds)
        {
            Check.True(CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(id, out var c) && c == CapitalNpcShopCurrency.Honor,
                $"Point Exchanger item {id} charges honour");
        }
        Check.True(CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(9958, out var medal) && medal == CapitalNpcShopCurrency.Medal,
            "medal rows unchanged");
        Check.True(CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(9950, out var point) && point == CapitalNpcShopCurrency.Point,
            "point rows unchanged");

        foreach (var (key, id) in new[] { ("Athens_036", 5175u), ("Athens_Newbie_004", 5285u), ("Sparta_Newbie_004", 46565u) })
        {
            Check.True(CapitalNpcServiceProtocol.TryResolve(key, id, out var kind) && kind == CapitalNpcServiceKind.SkillVendor &&
                       CapitalNpcServiceProtocol.TryGetShopCurrency(kind, out var currency) && currency == CapitalNpcShopCurrency.Silver,
                $"{key} is the skill shelf and charges silver");
        }
        Check.True(CapitalNpcServiceProtocol.TryResolve("Athens_021", 5161u, out var props) && props == CapitalNpcServiceKind.PropsVendor,
            "Athens_021 keeps its own (props) shelf");

        // Same item and price at the Athens skill shelf and Sparta's skill merchant, both in silver.
        Check.True(
            PacketBuilder.TryResolveCapitalNpcShopOffer(CapitalNpcServiceKind.SkillVendor, 0, 0, 5445, out var athensBook) &&
            athensBook.Currency == CapitalNpcShopCurrency.Silver && athensBook.UnitPrice == 1,
            "Athens skill shelf: Book Fireball 1 costs 1 silver");
        Check.True(
            PacketBuilder.TryResolveCapitalNpcShopOffer(CapitalNpcServiceKind.PropsVendor, 1, 0, 9010, out var ware) &&
            ware.Currency == CapitalNpcShopCurrency.Silver && ware.UnitPrice == 20_000,
            "Athens props shelf (compiled): Bronze Ware 20,000 silver, not B-Gold");
        return Task.CompletedTask;
    }
}
