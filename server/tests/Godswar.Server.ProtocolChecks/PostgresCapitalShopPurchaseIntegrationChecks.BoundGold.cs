using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCapitalShopPurchaseIntegrationChecks
{
    private static async Task CheckBoundGoldVendorPurchaseAsync(
        NpgsqlDataSource dataSource, PostgresGameStore store)
    {
        Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(
            CapitalNpcServiceKind.BoundGoldVendor, 1, 30, 11005, out var offer) &&
            offer.UnitPrice == 5_000 &&
            offer.Currency == CapitalNpcShopCurrency.BindingGold,
            "Phoenix Feather resolves its authorized B-Gold price");
        var fixture = await CreateFixtureAsync(dataSource, 777, 80_000, 20_000);
        var operation = Guid.NewGuid();
        var result = await store.PurchaseCapitalShopItemAsync(
            fixture.AccountId, fixture.CharacterId, operation, offer, 2);
        Check.True(result.Purchased && result.CurrencyBalance == 10_000 &&
            result.Character?.BindingGold == 10_000 &&
            result.Character?.Gold == 80_000 && result.Character?.Silver == 777,
            "two feathers debit 10,000 B-Gold and preserve Gold and Silver");
        try
        {
            await store.PurchaseCapitalShopItemAsync(
                fixture.AccountId, fixture.CharacterId, operation, offer, 2);
        }
        catch (PostgresException error) when (
            error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Duplicate command mutations must roll back atomically.
        }
        var state = await ReadStateAsync(dataSource, fixture, 11005);
        Check.True(state.BindingGold == 10_000 && state.Gold == 80_000 &&
            state.Silver == 777 && state.PurchasedStack == 2 &&
            state.WalletRevision == 1 && state.InventoryRevision == 1 &&
            state.CurrencyCode == "binding_gold" && state.Delta == -10_000 &&
            state.BalanceBefore == 20_000 && state.BalanceAfter == 10_000 &&
            state.RequestCurrency == nameof(CapitalNpcShopCurrency.BindingGold) &&
            state.ResultCurrency == nameof(CapitalNpcShopCurrency.BindingGold),
            "B-Gold ledger and inventory commit exactly once across duplicate purchase");

        var insufficient = await store.PurchaseCapitalShopItemAsync(
            fixture.AccountId, fixture.CharacterId, Guid.NewGuid(), offer, 3);
        Check.True(insufficient.Status == CapitalShopPurchaseStatus.InsufficientCurrency,
            "vendor cannot spend Gold when B-Gold cannot cover the order");
        var after = await ReadStateAsync(dataSource, fixture, 11005);
        Check.True(state == after,
            "insufficient B-Gold purchase changes no wallet, inventory, or ledger state");
        await CheckSocketSpellVendorPurchasesAsync(dataSource, store);
        await CheckHolySuitWareVendorPurchasesAsync(dataSource, store);
        await CheckAscensionCoreVendorPurchasesAsync(dataSource, store);
    }

    private static async Task CheckSocketSpellVendorPurchasesAsync(NpgsqlDataSource dataSource, PostgresGameStore store)
    {
        foreach (var (id, price, index) in new[] { (4272u, 392_000, 33), (4273u, 1_568_000, 34) })
        {
            Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(CapitalNpcServiceKind.BoundGoldVendor,
                2, index, id, out var offer) && offer.UnitPrice == price &&
                offer.Currency == CapitalNpcShopCurrency.BindingGold,
                "new Socket Spells resolve the approved price at the final sorted listing");
            var fixture = await CreateFixtureAsync(dataSource, 777, 80_000, 4_000_000);
            var purchased = await store.PurchaseCapitalShopItemAsync(fixture.AccountId, fixture.CharacterId,
                Guid.NewGuid(), offer, 2);
            var expectedBalance = 4_000_000 - 2 * price;
            var state = await ReadStateAsync(dataSource, fixture, checked((int)id));
            Check.True(purchased.Purchased && state.PurchasedStack == 2 && state.BindingGold == expectedBalance &&
                state.Gold == 80_000 && state.Silver == 777 && state.CurrencyCode == "binding_gold" &&
                state.Delta == -2 * price && state.WalletRevision == 1 && state.InventoryRevision == 1,
                "Socket Spell purchase debits only the approved B-Gold amount and grants exactly two units");
        }
    }

    private static async Task CheckHolySuitWareVendorPurchasesAsync(NpgsqlDataSource dataSource, PostgresGameStore store)
    {
        int[] prices = [23,115,575,1152,3152,8954,15673,31346];
        for (var tier = 0; tier < 8; tier++)
        {
            var id = checked((uint)(9010 + tier));
            Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(CapitalNpcServiceKind.BoundGoldVendor,
                3, tier, id, out var offer) && offer.UnitPrice == prices[tier],
                "each single ware resolves in complete tier order at its authorized price");
            var fixture = await CreateFixtureAsync(dataSource, 777, 80_000, 1_000_000);
            var purchased = await store.PurchaseCapitalShopItemAsync(fixture.AccountId, fixture.CharacterId,
                Guid.NewGuid(), offer, 1);
            var state = await ReadStateAsync(dataSource, fixture, checked((int)id));
            Check.True(purchased.Purchased && state.PurchasedStack == 1 && state.BindingGold == 1_000_000 - prices[tier] &&
                state.Gold == 80_000 && state.Silver == 777 && state.Delta == -prices[tier],
                "a single ware order grants exactly one and spends only its B-Gold unit price");
        }
        Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(CapitalNpcServiceKind.BoundGoldVendor,
            3, 17, 9017, out var bulk) && bulk.UnitPrice == 31_346, "Divinium has the matching bulk quantity preset");
        var owner = await CreateFixtureAsync(dataSource, 777, 80_000, 1_000_000);
        var result = await store.PurchaseCapitalShopItemAsync(owner.AccountId, owner.CharacterId, Guid.NewGuid(), bulk, 25);
        var bulkState = await ReadStateAsync(dataSource, owner, 9017);
        Check.True(result.Purchased && bulkState.PurchasedStack == 25 && bulkState.Delta == -783_650 &&
            bulkState.BindingGold == 216_350 && bulkState.Gold == 80_000 && bulkState.Silver == 777,
            "the Divinium bulk preset buys exactly requested25 units at25 times the approved per-unit price");
    }
}
