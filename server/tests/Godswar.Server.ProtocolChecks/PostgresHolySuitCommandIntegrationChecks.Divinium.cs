using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolySuitCommandIntegrationChecks
{
    public const string DiviniumCheckName =
        "PostgreSQL Divinium upgrades preserve old equipment and consume exact wares and prisms once";

    public static async Task RunDiviniumAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString)) throw new CheckSkippedException(DiviniumCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!DisposableDatabasePattern.IsMatch(database))
                throw new CheckSkippedException(DiviniumCheckName + " requires a disposable B03/B09 database");
        }
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var game = new PostgresGameStore(connectionString);
        await game.EnsureSeedDataAsync();
        var fixture = await CreateFixtureAsync(connectionString);
        await using (var prepare = source.CreateCommand("""
            UPDATE public.character_items SET holy_suit_code=710 WHERE user_id=@character AND item_location=1 AND slot_index=2;
            UPDATE public.character_items SET prop_id=9017 WHERE user_id=@character AND item_location=1 AND slot_index=4;
            """))
        {
            prepare.Parameters.AddWithValue("character", fixture.CharacterId);
            Check.Equal(2, await prepare.ExecuteNonQueryAsync(), "prepare existing Seraphite10 gear and the new Divinium ware");
        }
        var executor = new PostgresHolySuitCommandExecutor(source, new PostgresOutboxDispatcherOptions(),
            game.ItemContent, TestRealmCalendar());
        var correlation = new CommandConnectionCorrelation(Guid.NewGuid(), CommandTransportKind.SecureTlsLegacy);
        var gear = Item(1007, suit: 710).ToCompactString();
        var ware = Item(9017, stack: 99).ToCompactString();
        var insufficient = await ExecuteAsync(executor, fixture, correlation, Guid.NewGuid(),
            HolySuitCommandOperation.ConsumeWare, primarySlot: 2, primaryState: gear, secondarySlot: 4, secondaryState: ware);
        Require(insufficient, HolySuitExecutionDisposition.TerminalRejected, HolySuitCommandResultStatus.InsufficientPrisms,
            "tier7:10 is upgradeable but twenty prisms cannot pay the ninety-nine-prism first step");
        Check.Equal(710, await ReadDiviniumCodeAsync(source, fixture.CharacterId),
            "insufficient-prism rejection preserves the original owned710 state");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            for (short slot = 8; slot <= 20; slot++)
                await InsertItemAsync(connection, transaction, fixture.CharacterId, slot, Item(9025, bound: 1, stack: 99));
            await transaction.CommitAsync();
        }
        var inventoryBefore = await ReadDiviniumInventoryAsync(source, fixture.CharacterId);
        for (var level = 1; level <= 10; level++)
        {
            var operationId = Guid.NewGuid();
            var beforeGear = gear;
            var beforeWare = ware;
            var result = await ExecuteAsync(executor, fixture, correlation, operationId, HolySuitCommandOperation.ConsumeWare,
                primarySlot: 2, primaryState: beforeGear, secondarySlot: 4, secondaryState: beforeWare);
            var receipt = Require(result, HolySuitExecutionDisposition.Committed, HolySuitCommandResultStatus.WareConsumed,
                "durable Divinium level " + level);
            gear = receipt.Mutations.Single(value => value.Role == HolySuitReceiptItemRole.Equipment).AfterCompactItemState;
            ware = receipt.Mutations.Single(value => value.Role == HolySuitReceiptItemRole.Ware).AfterCompactItemState;
            Check.True(CompactItemEntry.Parse(gear).HolySuitCode == 800 + level && receipt.PrismsConsumed == 96 + 3 * level &&
                CompactItemEntry.Parse(beforeWare).Stack - CompactItemEntry.Parse(ware).Stack == level,
                "each Divinium step writes its real 8xx code with the published prism and target-level ware cost");
            var after = await ReadDiviniumInventoryAsync(source, fixture.CharacterId);
            var replay = await ExecuteAsync(executor, fixture, correlation, operationId, HolySuitCommandOperation.ConsumeWare,
                primarySlot: 2, primaryState: beforeGear, secondarySlot: 4, secondaryState: beforeWare);
            Check.True(replay.Disposition == HolySuitExecutionDisposition.Duplicate && replay.Receipt!.PrismsConsumed == receipt.PrismsConsumed &&
                replay.Receipt.Mutations.SequenceEqual(receipt.Mutations) &&
                await ReadDiviniumInventoryAsync(source, fixture.CharacterId) == after,
                "each committed Divinium upgrade replays without another ware, prism, or inventory revision debit");
        }
        var final = await ReadDiviniumInventoryAsync(source, fixture.CharacterId);
        Check.True(final.Prisms == inventoryBefore.Prisms - 1125 && final.Wares == 44 &&
            final.Revision == inventoryBefore.Revision + 10 && final.Code == 810,
            "all ten durable steps consume exactly 1125 prisms and 55 wares while preserving the same equipment item");
        var maximum = await ExecuteAsync(executor, fixture, correlation, Guid.NewGuid(), HolySuitCommandOperation.ConsumeWare,
            primarySlot: 2, primaryState: gear, secondarySlot: 4, secondaryState: ware);
        Require(maximum, HolySuitExecutionDisposition.TerminalRejected, HolySuitCommandResultStatus.MaximumHolySuit,
            "Divinium10 is the authoritative new terminal state");
        Check.Equal(final, await ReadDiviniumInventoryAsync(source, fixture.CharacterId),
            "maximum-tier rejection preserves every item, cost, and inventory revision");
    }

    private static async Task<int> ReadDiviniumCodeAsync(NpgsqlDataSource source, int characterId) =>
        (await ReadDiviniumInventoryAsync(source, characterId)).Code;

    private static async Task<(int Code, int Wares, int Prisms, long Revision)> ReadDiviniumInventoryAsync(
        NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT (SELECT holy_suit_code FROM character_items WHERE user_id=@character AND item_location=1 AND slot_index=2),
                   (SELECT stack FROM character_items WHERE user_id=@character AND item_location=1 AND slot_index=4),
                   (SELECT COALESCE(sum(stack),0)::integer FROM character_items WHERE user_id=@character AND prop_id=9025),
                   inventory_revision FROM character_base WHERE id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "read exact durable Divinium inventory state");
        return (reader.GetInt32(0), reader.GetInt16(1), reader.GetInt32(2), reader.GetInt64(3));
    }
}
