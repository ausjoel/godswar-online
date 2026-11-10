using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCapitalShopPurchaseIntegrationChecks
{
    private static async Task CheckAscensionCoreVendorPurchasesAsync(
        NpgsqlDataSource source, PostgresGameStore store)
    {
        Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(
            CapitalNpcServiceKind.BoundGoldVendor, 3, 19, 9025, out var offer) &&
            offer.UnitPrice == 50_000 && offer.Currency == CapitalNpcShopCurrency.BindingGold &&
            offer.Item.Bound == 1 && offer.Item.Stack == 1,
            "Ascension Core follows Holy Box V at 50,000 B-Gold per bound unit");
        foreach (var quantity in new[] { 1, 2 })
        {
            var fixture = await CreateFixtureAsync(source, 777, 80_000, 150_000);
            var result = await store.PurchaseCapitalShopItemAsync(
                fixture.AccountId, fixture.CharacterId, Guid.NewGuid(), offer, quantity);
            var state = await ReadStateAsync(source, fixture, 9025);
            var cost = quantity * 50_000;
            Check.True(result.Purchased && result.CurrencyBalance == 150_000 - cost &&
                state.PurchasedStack == quantity && state.BindingGold == 150_000 - cost &&
                state.Gold == 80_000 && state.Silver == 777 && state.CurrencyCode == "binding_gold" &&
                state.Delta == -cost && state.BalanceBefore == 150_000 && state.BalanceAfter == 150_000 - cost &&
                state.WalletRevision == 1 && state.InventoryRevision == 1,
                "one and two cores grant exactly requested units and debit only 50,000 B-Gold each");
            await using var bound = source.CreateCommand("""
                SELECT count(*)=1 AND bool_and(bound=1 AND stack=@quantity)
                FROM character_items WHERE user_id=@character AND prop_id=9025;
                """);
            bound.Parameters.AddWithValue("quantity", quantity);
            bound.Parameters.AddWithValue("character", fixture.CharacterId);
            Check.True(await bound.ExecuteScalarAsync() is true,
                "purchased cores persist the same bound flag as EXP-created cores");
        }
    }
}
