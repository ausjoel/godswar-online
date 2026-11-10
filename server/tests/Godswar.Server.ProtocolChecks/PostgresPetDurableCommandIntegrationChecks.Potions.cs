using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string PotionCheckName = "PostgreSQL potions atomically heal consume enforce owner and preserve replay after damage";
    public static async Task RunPotionsAsync()
    {
        var connection = ReadRequiredConnectionString();
        await using var source = NpgsqlDataSource.Create(connection);
        await AssertDisposableDatabaseAsync(source);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var game = new PostgresGameStore(connection); await game.EnsureSeedDataAsync();
        var merge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(source);
        PostgresPetDurableCommandExecutor Executor() => new(source, new PostgresOutboxDispatcherOptions(),
            game.ItemContent, game.PetContent, merge, PetLearnedSkillContentBaseline.Create());
        var fixture = await CreateFixtureAsync(connection);
        var context = new PotionVitalsSnapshot(54000, 9900, 55000, 10000, 100);
        foreach (var pair in new[] { (4000, 200, 0), (4001, 500, 0), (4030, 0, 60) })
        {
            await SeedPotionAsync(source, fixture, pair.Item1, context);
            var before = await ReadPotionStateAsync(source, fixture.CharacterId);
            var ledger = await ReadSackEvidenceAsync(source, fixture.CharacterId);
            var operation = PotionEnvelope(fixture, checked((uint)pair.Item1), context);
            var executor = Executor();
            var results = await Task.WhenAll(executor.ExecuteAsync(operation), executor.ExecuteAsync(operation));
            AssertCommitAndDuplicate(results, PetDurableReceiptStatus.PotionUsed, "concurrent duplicate potion use");
            var after = await ReadPotionStateAsync(source, fixture.CharacterId);
            Check.Equal(before with { Hp = before.Hp + pair.Item2, Mp = before.Mp + pair.Item3,
                VitalsRevision = 101, Stack = 1, InventoryRevision = before.InventoryRevision + 1 }, after,
                "one potion heals against calculated maxima while base maxima currencies and unrelated state remain intact");
            await AssertSackEvidenceDeltaAsync(source, fixture.CharacterId, ledger, ledger: 1);
            Check.True(results[0].Receipt!.Potion!.IsValid, "committed potion retains exact settlement proof");
            // Simulate subsequent damage and a mana cost, then replay after creating a new executor.
            await using (var damage = source.CreateCommand("""
                UPDATE character_base SET "curHP"="curHP"-1000, "curMP"="curMP"-30,
                vitals_revision=vitals_revision+2 WHERE id=@character;
                """))
            { damage.Parameters.AddWithValue("character", fixture.CharacterId); await damage.ExecuteNonQueryAsync(); }
            var damaged = await ReadPotionStateAsync(source, fixture.CharacterId);
            var replay = await Executor().ExecuteAsync(operation);
            Check.True(replay.Disposition == PetDurableExecutionDisposition.Duplicate, "restart/unknown-return retry finds committed receipt");
            Check.Equal(results[0].Receipt!, replay.Receipt!, "historical settlement proof is stable");
            Check.Equal(damaged, await ReadPotionStateAsync(source, fixture.CharacterId), "replay cannot heal away later damage or consume again");
            var skill = pair.Item1 == 4030 ? 3120 : 3100 + pair.Item1 - 4000;
            await ExtendConsumableCooldownForAssertionAsync(source, fixture.CharacterId, skill);
            var cooling = await Executor().ExecuteAsync(PotionEnvelope(fixture, checked((uint)pair.Item1),
                context with { Hp = damaged.Hp, Mp = damaged.Mp, Revision = damaged.VitalsRevision }));
            Check.True(cooling.Receipt!.Status == PetDurableReceiptStatus.ConsumableCooldownActive, "native cooldown applies");
            Check.Equal(damaged, await ReadPotionStateAsync(source, fixture.CharacterId), "cooldown cannot heal or consume");
        }
        var partial = context with { Hp = 54950 };
        await SeedPotionAsync(source, fixture, 4000, partial);
        Check.True((await Executor().ExecuteAsync(PotionEnvelope(fixture, 4000, partial))).IsSuccess, "partial HP potion commits");
        Check.Equal(55000, (await ReadPotionStateAsync(source, fixture.CharacterId)).Hp, "partial HP heal caps at calculated max");
        foreach (var item in new[] { 4000, 4030 })
        {
            var full = context with { Hp = 55000, Mp = 10000 };
            await SeedPotionAsync(source, fixture, item, full);
            var before = await ReadPotionStateAsync(source, fixture.CharacterId);
            var result = await Executor().ExecuteAsync(PotionEnvelope(fixture, checked((uint)item), full));
            Check.True(result.Receipt!.Status == PetDurableReceiptStatus.PotionNotNeeded, "full resource rejects potion");
            Check.Equal(before, await ReadPotionStateAsync(source, fixture.CharacterId), "full resource consumes nothing");
            await AssertPotionCooldownNotStartedAsync(source, fixture.CharacterId);
        }
        await SeedPotionAsync(source, fixture, 4000, context);
        var unchanged = await ReadPotionStateAsync(source, fixture.CharacterId);
        foreach (var bad in new PotionUseContext?[] { null, new(4001, context), new(4000, context with { Hp = 53000 }),
            new(4000, context with { Mp = 9800 }), new(4000, context with { Revision = 99 }) })
        {
            var operation = SackEnvelope(fixture);
            var result = await Executor().ExecuteAsync(operation with { Command = operation.Command with { PotionContext = bad } });
            Check.True(result.Receipt!.Status == PetDurableReceiptStatus.PotionContextMismatch, "missing stale or mismatched session context rejected");
            Check.Equal(unchanged, await ReadPotionStateAsync(source, fixture.CharacterId), "context mismatch cannot consume or change health");
            await AssertPotionCooldownNotStartedAsync(source, fixture.CharacterId);
        }
        // An editor/bag change while a session reserves potion use must not activate a different item.
        await using (var swap = source.CreateCommand("UPDATE character_items SET prop_id=4150 WHERE user_id=@character AND item_location=1;"))
        { swap.Parameters.AddWithValue("character", fixture.CharacterId); await swap.ExecuteNonQueryAsync(); }
        var swapped = await ReadPotionStateAsync(source, fixture.CharacterId);
        var swappedResult = await Executor().ExecuteAsync(PotionEnvelope(fixture, 4000, context));
        Check.True(swappedResult.Receipt!.Status == PetDurableReceiptStatus.PotionContextMismatch,
            "potion reservation cannot become a money-bag activation after concurrent item change");
        Check.Equal(swapped, await ReadPotionStateAsync(source, fixture.CharacterId), "changed item neither consumed nor rewarded");
        await SeedPotionAsync(source, fixture, 4000, context);
        unchanged = await ReadPotionStateAsync(source, fixture.CharacterId);
        var unfenced = PotionEnvelope(fixture, 4000, context);
        var ownerRejected = false;
        try { await Executor().ExecuteAsync(unfenced with { Ownership = unfenced.Ownership with { Generation = 2 } }); }
        catch (PlayerOwnershipValidationException) { ownerRejected = true; }
        Check.True(ownerRejected, "lost session ownership blocks potion execution");
        Check.Equal(unchanged, await ReadPotionStateAsync(source, fixture.CharacterId), "lost owner cannot consume or heal");
        await SeedPotionAsync(source, fixture, 4000, context with { Hp = 0 });
        var dead = await ReadPotionStateAsync(source, fixture.CharacterId);
        var deadResult = await Executor().ExecuteAsync(PotionEnvelope(fixture, 4000, context));
        Check.True(deadResult.Receipt!.Status == PetDurableReceiptStatus.PotionContextMismatch, "stale alive context cannot resurrect DB-dead character");
        Check.Equal(dead, await ReadPotionStateAsync(source, fixture.CharacterId), "dead character is not healed and item remains");
        await CheckPotionRollbackAsync(source, fixture, context, Executor);
    }

    private static async Task CheckPotionRollbackAsync(NpgsqlDataSource source, PetFixture fixture,
        PotionVitalsSnapshot context, Func<PostgresPetDurableCommandExecutor> executor)
    {
        await SeedPotionAsync(source, fixture, 4000, context);
        var before = await ReadPotionStateAsync(source, fixture.CharacterId);
        var ledger = await ReadSackEvidenceAsync(source, fixture.CharacterId);
        var operation = PotionEnvelope(fixture, 4000, context);
        await using (var trigger = source.CreateCommand("""
            CREATE FUNCTION public.reject_potion_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected potion inventory failure'; END; $$;
            CREATE TRIGGER reject_potion_test BEFORE INSERT ON public.character_inventory_ledger
            FOR EACH ROW WHEN (NEW.reason_code='bag_potion_consumed') EXECUTE FUNCTION public.reject_potion_test();
            """)) await trigger.ExecuteNonQueryAsync();
        try
        {
            var rejected = false;
            try { await executor().ExecuteAsync(operation); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { rejected = true; }
            Check.True(rejected, "injected ledger fault aborts potion settlement");
            Check.Equal(before, await ReadPotionStateAsync(source, fixture.CharacterId), "heal revision and item roll back together");
            Check.Equal(ledger, await ReadSackEvidenceAsync(source, fixture.CharacterId), "audit inbox outbox and ledgers roll back together");
            await AssertPotionCooldownNotStartedAsync(source, fixture.CharacterId);
        }
        finally
        {
            await using var cleanup = source.CreateCommand("DROP TRIGGER reject_potion_test ON public.character_inventory_ledger; DROP FUNCTION public.reject_potion_test();");
            await cleanup.ExecuteNonQueryAsync();
        }
        Check.True((await executor().ExecuteAsync(operation)).IsSuccess, "same operation commits once after fault removed");
        Check.Equal(before with { Hp = 54200, VitalsRevision = 101, Stack = 1,
            InventoryRevision = before.InventoryRevision + 1 }, await ReadPotionStateAsync(source, fixture.CharacterId),
            "retry after rollback has exactly one effect");
    }

    private static CommandEnvelope<BagItemActivationCommand> PotionEnvelope(PetFixture fixture, uint item,
        PotionVitalsSnapshot context)
    {
        var envelope = SackEnvelope(fixture);
        return envelope with { Command = envelope.Command with { PotionContext = new(item, context) } };
    }

    private static async Task SeedPotionAsync(NpgsqlDataSource source, PetFixture fixture, int item, PotionVitalsSnapshot context)
    {
        await using var command = source.CreateCommand("""
            DELETE FROM character_items WHERE user_id=@character AND item_location=1;
            DELETE FROM character_bag_consumable_cooldowns WHERE character_id=@character AND cooldown_group BETWEEN 3100 AND 3125;
            UPDATE character_base SET "curHP"=@hp,"curMP"=@mp,"MaxHP"=1500,"MaxMP"=300,vitals_revision=@revision WHERE id=@character;
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
            VALUES(@character,1,@slot,@item,0,10,0,2);
            """);
        command.Parameters.AddWithValue("character", fixture.CharacterId); command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("slot", checked((short)fixture.EggSlot)); command.Parameters.AddWithValue("hp", context.Hp);
        command.Parameters.AddWithValue("mp", context.Mp); command.Parameters.AddWithValue("revision", context.Revision);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertPotionCooldownNotStartedAsync(NpgsqlDataSource source, int character)
    {
        await using var command = source.CreateCommand("""
            SELECT count(*) FROM character_bag_consumable_cooldowns WHERE character_id=@character
            AND cooldown_group BETWEEN 3100 AND 3125 AND ready_at > transaction_timestamp();
            """); command.Parameters.AddWithValue("character", character);
        Check.Equal(0L, (long)(await command.ExecuteScalarAsync())!, "rejection or rollback cannot start successful-use cooldown");
    }

    private static async Task<PotionState> ReadPotionStateAsync(NpgsqlDataSource source, int character)
    {
        await using var command = source.CreateCommand("""
            SELECT "curHP","curMP",vitals_revision,inventory_revision,"MaxHP","MaxMP","Money",wallet_revision,
            COALESCE((SELECT sum(stack) FROM character_items WHERE user_id=@character AND item_location=1),0)
            FROM character_base WHERE id=@character;
            """); command.Parameters.AddWithValue("character", character);
        await using var reader = await command.ExecuteReaderAsync(); Check.True(await reader.ReadAsync(), "potion state exists");
        return new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt64(2),reader.GetInt64(3),
            reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6),reader.GetInt64(7),reader.GetInt64(8));
    }
    private sealed record PotionState(int Hp,int Mp,long VitalsRevision,long InventoryRevision,int BaseMaxHp,
        int BaseMaxMp,int Silver,long WalletRevision,long Stack);
}
