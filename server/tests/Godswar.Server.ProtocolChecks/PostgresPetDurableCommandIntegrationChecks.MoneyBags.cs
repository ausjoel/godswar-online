using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string MoneyBagCheckName="PostgreSQL money bags settle once with wallet caps cooldown and atomic rollback";
    public static async Task RunMoneyBagsAsync()
    {
        var connection=ReadRequiredConnectionString();await using var source=NpgsqlDataSource.Create(connection);
        await AssertDisposableDatabaseAsync(source);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var game=new PostgresGameStore(connection);await game.EnsureSeedDataAsync();
        var merge=await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(source);
        PostgresPetDurableCommandExecutor Executor()=>new(source,new PostgresOutboxDispatcherOptions(),game.ItemContent,
            game.PetContent,merge,PetLearnedSkillContentBaseline.Create());
        var fixture=await CreateFixtureAsync(connection);
        foreach(var pair in new[]{(4150,10000),(4151,100000),(4152,500000)})
        {
            await SeedMoneyBagAsync(source,fixture,pair.Item1,1234);
            var before=await ReadMoneyBagStateAsync(source,fixture.CharacterId);
            var evidenceBefore=await ReadSackEvidenceAsync(source,fixture.CharacterId);
            var envelope=SackEnvelope(fixture);var service=Executor();
            var results=await Task.WhenAll(service.ExecuteAsync(envelope),service.ExecuteAsync(envelope));
            AssertCommitAndDuplicate(results,PetDurableReceiptStatus.MoneyBagOpened,"concurrent money-bag use");
            var after=await ReadMoneyBagStateAsync(source,fixture.CharacterId);
            Check.Equal(before with{Silver=before.Silver+pair.Item2,Stack=1,InventoryRevision=before.InventoryRevision+1,
                WalletRevision=before.WalletRevision+1,WalletLedgerCount=before.WalletLedgerCount+1},after,"one bag credits exact silver and leaves other counters/vitals intact");
            await AssertSackEvidenceDeltaAsync(source,fixture.CharacterId,evidenceBefore,ledger:1);
            var replay=await Executor().ExecuteAsync(envelope);
            Check.True(replay.Disposition==PetDurableExecutionDisposition.Duplicate,"new executor replays receipt after restart");
            Check.Equal(results[0].Receipt!,replay.Receipt!,"exact money settlement proof replayed");
            Check.Equal(after,await ReadMoneyBagStateAsync(source,fixture.CharacterId),"duplicate never credits or consumes twice");
            await ExtendConsumableCooldownForAssertionAsync(source,fixture.CharacterId,4600+pair.Item1-4150);
            var cooldown=await Executor().ExecuteAsync(SackEnvelope(fixture));
            Check.True(cooldown.Receipt!.Status==PetDurableReceiptStatus.ConsumableCooldownActive,"native cooldown enforced");
            Check.Equal(after,await ReadMoneyBagStateAsync(source,fixture.CharacterId),"cooldown rejection preserves inventory and wallet");
        }
        await SeedMoneyBagAsync(source,fixture,4150,int.MaxValue-9999);
        var fullBefore=await ReadMoneyBagStateAsync(source,fixture.CharacterId);
        var full=await Executor().ExecuteAsync(SackEnvelope(fixture));
        Check.True(full.Receipt!.Status==PetDurableReceiptStatus.MoneyBagWalletFull,"insufficient full-reward headroom refused");
        Check.Equal(fullBefore,await ReadMoneyBagStateAsync(source,fixture.CharacterId),"wallet-full rejection consumes nothing");
        await SeedMoneyBagAsync(source,fixture,4150,int.MaxValue-10000);
        Check.True((await Executor().ExecuteAsync(SackEnvelope(fixture))).IsSuccess,"exact wallet ceiling can be credited");
        Check.Equal(int.MaxValue,(await ReadMoneyBagStateAsync(source,fixture.CharacterId)).Silver,"no signed overflow at ceiling");
        await SeedMoneyBagAsync(source,fixture,4150,1234);
        var rollbackBefore=await ReadMoneyBagStateAsync(source,fixture.CharacterId);
        var evidence=await ReadSackEvidenceAsync(source,fixture.CharacterId);var operation=SackEnvelope(fixture);
        await using(var trigger=source.CreateCommand("""
            CREATE FUNCTION public.reject_money_bag_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected money bag inventory failure'; END; $$;
            CREATE TRIGGER reject_money_bag_test BEFORE INSERT ON public.character_inventory_ledger
            FOR EACH ROW WHEN (NEW.reason_code='money_bag_consumed') EXECUTE FUNCTION public.reject_money_bag_test();
            """))await trigger.ExecuteNonQueryAsync();
        try
        {
            var rejected=false;try{await Executor().ExecuteAsync(operation);}
            catch(PostgresException ex) when(ex.SqlState==PostgresErrorCodes.RaiseException){rejected=true;}
            Check.True(rejected,"injected ledger fault aborts the transaction");
            Check.Equal(rollbackBefore,await ReadMoneyBagStateAsync(source,fixture.CharacterId),"wallet and bag roll back together");
            Check.Equal(evidence,await ReadSackEvidenceAsync(source,fixture.CharacterId),"no partial command audit inbox ledger or outbox persists");
        }
        finally
        {
            await using var cleanup=source.CreateCommand("DROP TRIGGER reject_money_bag_test ON public.character_inventory_ledger; DROP FUNCTION public.reject_money_bag_test();");
            await cleanup.ExecuteNonQueryAsync();
        }
        Check.True((await Executor().ExecuteAsync(operation)).IsSuccess,"same operation succeeds after rolled-back failure removal");
        Check.Equal(rollbackBefore with{Silver=11234,Stack=1,WalletRevision=rollbackBefore.WalletRevision+1,
            InventoryRevision=rollbackBefore.InventoryRevision+1,WalletLedgerCount=rollbackBefore.WalletLedgerCount+1},await ReadMoneyBagStateAsync(source,fixture.CharacterId),"retried failed use commits exactly once");
    }
    private static async Task SeedMoneyBagAsync(NpgsqlDataSource source,PetFixture fixture,int item,int silver)
    {
        await using var command=source.CreateCommand("""
            DELETE FROM character_items WHERE user_id=@character AND item_location=1;
            DELETE FROM character_bag_consumable_cooldowns WHERE character_id=@character AND cooldown_group BETWEEN 4600 AND 4602;
            UPDATE character_base SET "Money"=@silver WHERE id=@character;
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
            VALUES(@character,1,@slot,@item,0,10,0,2);
            """);
        command.Parameters.AddWithValue("character",fixture.CharacterId);command.Parameters.AddWithValue("silver",silver);
        command.Parameters.AddWithValue("slot",checked((short)fixture.EggSlot));command.Parameters.AddWithValue("item",item);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<MoneyBagState> ReadMoneyBagStateAsync(NpgsqlDataSource source,int character)
    {
        await using var command=source.CreateCommand("""
            SELECT "Money","Stone","BindingGold",wallet_revision,inventory_revision,"curHP","curMP",vitals_revision,
            COALESCE((SELECT sum(stack) FROM character_items WHERE user_id=@character AND item_location=1 AND prop_id BETWEEN 4150 AND 4152),0),
            (SELECT count(*) FROM character_currency_ledger WHERE character_id=@character)
            FROM character_base WHERE id=@character;
            """);command.Parameters.AddWithValue("character",character);
        await using var r=await command.ExecuteReaderAsync();Check.True(await r.ReadAsync(),"money-bag state exists");
        return new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt64(3),r.GetInt64(4),r.GetInt32(5),r.GetInt32(6),r.GetInt64(7),r.GetInt64(8),r.GetInt64(9));
    }
    private sealed record MoneyBagState(int Silver,int Gold,int BoundGold,long WalletRevision,long InventoryRevision,
        int Hp,int Mp,long VitalsRevision,long Stack,long WalletLedgerCount);
}
