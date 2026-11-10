using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolySuitCommandIntegrationChecks
{
    internal static async Task AssertVendorCoreConsumptionAsync(string connectionString, PostgresGameStore store)
    {
        var fixture = await CreateFixtureAsync(connectionString, bindingGold: 5_000_000, initialPrisms: 0);
        await using var source = NpgsqlDataSource.Create(connectionString);
        Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(CapitalNpcServiceKind.BoundGoldVendor,
            3, 19, 9025, out var offer), "resolve the actual Ascension Core vendor listing");
        var bought = await store.PurchaseCapitalShopItemAsync(fixture.AccountId, fixture.CharacterId,
            Guid.NewGuid(), offer, 100);
        var before = await ReadVendorCoreStateAsync(source, fixture.CharacterId);
        Check.True(bought.Purchased && bought.CurrencyBalance == 0 && before.Cores == 100 &&
            before.Stacks == "99,1" && before.AllBound && before.BindingGold == 0 &&
            before.WalletRevision == 1 && before.InventoryRevision == 1 && before.Experience == 4_000_000_000L,
            "100 purchased cores split at the published 99 cap, all bound, with exact 5m B-Gold and no EXP debit");

        var executor = new PostgresHolySuitCommandExecutor(source, new PostgresOutboxDispatcherOptions(),
            store.ItemContent, TestRealmCalendar());
        var correlation = new CommandConnectionCorrelation(Guid.NewGuid(), CommandTransportKind.SecureTlsLegacy);
        var operation = Guid.NewGuid();
        var result = await ExecuteAsync(executor, fixture, correlation, operation, HolySuitCommandOperation.ConsumeWare,
            primarySlot: 2, primaryState: Item(1007, suit: 501).ToCompactString(),
            secondarySlot: 4, secondaryState: Item(9014, stack: 99).ToCompactString());
        var receipt = Require(result, HolySuitExecutionDisposition.Committed, HolySuitCommandResultStatus.WareConsumed,
            "vendor-bought cores satisfy the existing RuneSteel1 to2 upgrade");
        var after = await ReadVendorCoreStateAsync(source, fixture.CharacterId);
        Check.True(receipt.PrismsConsumed == 12 && after.Code == 502 && after.Cores == 88 &&
            after.Stacks == "87,1" && after.Wares == 97 && after.AllBound &&
            after.BindingGold == 0 && after.WalletRevision == 1 && after.InventoryRevision == 2 &&
            after.Experience == before.Experience,
            "upgrade consumes exactly12 purchased cores and2 RuneSteel wares, preserving currencies and EXP");
        var replay = await ExecuteAsync(executor, fixture, correlation, operation, HolySuitCommandOperation.ConsumeWare,
            primarySlot: 2, primaryState: Item(1007, suit: 501).ToCompactString(),
            secondarySlot: 4, secondaryState: Item(9014, stack: 99).ToCompactString());
        Check.True(replay.Disposition == HolySuitExecutionDisposition.Duplicate &&
            replay.Receipt!.PrismsConsumed == 12 && await ReadVendorCoreStateAsync(source, fixture.CharacterId) == after,
            "replayed upgrade cannot consume purchased cores or wares twice");
    }

    private static async Task<VendorCoreState> ReadVendorCoreStateAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT (SELECT COALESCE(sum(stack),0)::integer FROM character_items WHERE user_id=@character AND prop_id=9025),
                   (SELECT string_agg(stack::text,',' ORDER BY slot_index) FROM character_items WHERE user_id=@character AND prop_id=9025),
                   (SELECT bool_and(bound=1) FROM character_items WHERE user_id=@character AND prop_id=9025),
                   (SELECT holy_suit_code FROM character_items WHERE user_id=@character AND item_location=1 AND slot_index=2),
                   (SELECT stack::integer FROM character_items WHERE user_id=@character AND item_location=1 AND slot_index=4),
                   "BindingGold", wallet_revision, inventory_revision, fighter_job_exp
            FROM character_base WHERE id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "read purchased-core inventory and wallet state");
        return new(reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8));
    }

    private sealed record VendorCoreState(int Cores, string Stacks, bool AllBound, int Code, int Wares,
        int BindingGold, long WalletRevision, long InventoryRevision, long Experience);
}
