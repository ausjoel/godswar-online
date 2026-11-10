using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string ProgressItemCheckName = "PostgreSQL progress items settle once with balance caps cooldown and atomic rollback";

    public static async Task RunProgressItemsAsync()
    {
        var connection = ReadRequiredConnectionString();
        await using var source = NpgsqlDataSource.Create(connection);
        await AssertDisposableDatabaseAsync(source);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var game = new PostgresGameStore(connection);
        await game.EnsureSeedDataAsync();
        var merge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(source);
        PostgresPetDurableCommandExecutor Executor() => new(source, new PostgresOutboxDispatcherOptions(), game.ItemContent,
            game.PetContent, merge, PetLearnedSkillContentBaseline.Create());
        var fixture = await CreateFixtureAsync(connection);
        var runStart = DateTime.UtcNow.AddSeconds(-5);

        foreach (var (item, skill, amount, ledger, read) in new (int, int, int, int, Func<ProgressState, long>)[] {
            (4160, 4610, 10, 1, s => s.Gold), (4163, 4613, 100, 1, s => s.Gold),
            (4164, 4614, 2009, 1, s => s.BindingGold), (4199, 4629, 50000, 1, s => s.BindingGold),
            (4180, 4630, 5, 0, s => s.Talent), (4183, 4633, 100, 0, s => s.Talent),
            (4190, 4640, 20, 1, s => s.Honor), (3944, 4751, 888, 1, s => s.Honor),
            (3957, 4757, 100, 1, s => s.Point) })
        {
            await SeedProgressItemAsync(source, fixture, item, skill, 2);
            var before = await ReadProgressStateAsync(source, fixture.CharacterId);
            var envelope = SackEnvelope(fixture);
            var service = Executor();
            var results = await Task.WhenAll(service.ExecuteAsync(envelope), service.ExecuteAsync(envelope));
            AssertCommitAndDuplicate(results, PetDurableReceiptStatus.ProgressItemUsed, "concurrent progress item use");
            var after = await ReadProgressStateAsync(source, fixture.CharacterId);
            Check.Equal(read(before) + amount, read(after), $"item {item} grants the exact amount once");
            Check.Equal(before.Silver, after.Silver, "silver untouched");
            Check.Equal(before.Stack - 1, after.Stack, "one item consumed");
            Check.Equal(before.Ledger + ledger, after.Ledger, "currency ledger row");
            Check.Equal(before.Hp, after.Hp, "vitals untouched");
            var replay = await Executor().ExecuteAsync(envelope);
            Check.True(replay.Disposition == PetDurableExecutionDisposition.Duplicate, "new executor replays receipt");
            Check.Equal(results[0].Receipt!, replay.Receipt!, "exact settlement proof replayed");
            Check.Equal(after, await ReadProgressStateAsync(source, fixture.CharacterId), "duplicate never grants or consumes twice");
            await ExtendConsumableCooldownForAssertionAsync(source, fixture.CharacterId, skill);
            var cooldown = await Executor().ExecuteAsync(SackEnvelope(fixture));
            Check.True(cooldown.Receipt!.Status == PetDurableReceiptStatus.ConsumableCooldownActive, "native cooldown enforced");
            Check.Equal(after, await ReadProgressStateAsync(source, fixture.CharacterId), "cooldown refusal changes nothing");
        }

        // The balance ceiling refuses without consuming.
        await SeedProgressItemAsync(source, fixture, 4160, 4610, 2);
        await SetBalanceAsync(source, fixture.CharacterId, "\"Stone\"", int.MaxValue - 9);
        var fullBefore = await ReadProgressStateAsync(source, fixture.CharacterId);
        var full = await Executor().ExecuteAsync(SackEnvelope(fixture));
        Check.True(full.Receipt!.Status == PetDurableReceiptStatus.ProgressItemBalanceFull, "insufficient headroom refused");
        Check.Equal(fullBefore, await ReadProgressStateAsync(source, fixture.CharacterId), "refusal consumes nothing");
        await SetBalanceAsync(source, fixture.CharacterId, "\"Stone\"", 0);

        // Talent elixirs and Passion Rose write their own boost channel.
        foreach (var (boostItem, boostSkill, kind, status, bonus) in new[] { (4523, 4741, 20, 580, 5000), (4504, 4812, 14, 570, 10000) })
        {
            await SeedProgressItemAsync(source, fixture, boostItem, boostSkill, 2);
            var boost = await Executor().ExecuteAsync(SackEnvelope(fixture));
            Check.True(boost.Receipt!.Status == PetDurableReceiptStatus.PetExperienceBoostActivated, $"boost item {boostItem} commits");
            await using var row = source.CreateCommand(
                "SELECT status_id, bonus_basis_points, remaining_online_ticks > 0 FROM public.character_experience_modifiers WHERE character_id=$1 AND kind=$2");
            row.Parameters.AddWithValue(fixture.CharacterId);
            row.Parameters.AddWithValue(kind);
            await using var reader = await row.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync(), "boost row written");
            Check.Equal(status, reader.GetInt32(0), "boost status");
            Check.Equal(bonus, reader.GetInt32(1), "boost size");
            Check.True(reader.GetBoolean(2), "boost has online time");
        }

        // A firework is consumed and moves no balance.
        await SeedProgressItemAsync(source, fixture, 3941, 4823, 2);
        var fireBefore = await ReadProgressStateAsync(source, fixture.CharacterId);
        var fire = await Executor().ExecuteAsync(SackEnvelope(fixture));
        Check.True(fire.Receipt!.Status == PetDurableReceiptStatus.ProgressItemUsed, "firework commits");
        Check.Equal(fireBefore with { Stack = fireBefore.Stack - 1 }, await ReadProgressStateAsync(source, fixture.CharacterId), "firework moves no balance");
        var fireReplay = await Executor().ExecuteAsync(SackEnvelope(fixture));
        Check.True(fireReplay.Receipt!.Status == PetDurableReceiptStatus.ConsumableCooldownActive || fireReplay.Receipt!.Status == PetDurableReceiptStatus.ProgressItemUsed, "second firework follows the native cooldown");

        // Guild tokens need a guild and move the guild balance only.
        await SeedProgressItemAsync(source, fixture, 4340, 4660, 2);
        var noGuildBefore = await ReadProgressStateAsync(source, fixture.CharacterId);
        var noGuild = await Executor().ExecuteAsync(SackEnvelope(fixture));
        Check.True(noGuild.Receipt!.Status == PetDurableReceiptStatus.ProgressItemNoGuild, "token without a guild refused");
        Check.Equal(noGuildBefore, await ReadProgressStateAsync(source, fixture.CharacterId), "refusal consumes nothing");
        long tokenGuild;
        await using (var add = source.CreateCommand("""
            WITH g AS (INSERT INTO public.guilds(name,lord_character_id,created_at,updated_at)
                VALUES('tokentest' || floor(random()*1e9)::text,$1,now(),now()) RETURNING id),
            m AS (INSERT INTO public.guild_members(character_id,guild_id,duty,joined_at) SELECT $1,id,1,now() FROM g)
            SELECT id FROM g;
            """))
        {
            add.Parameters.AddWithValue(fixture.CharacterId);
            tokenGuild = (long)(await add.ExecuteScalarAsync())!;
        }
        var token = await Executor().ExecuteAsync(SackEnvelope(fixture));
        Check.True(token.Receipt!.Status == PetDurableReceiptStatus.ProgressItemUsed, "silver token commits");
        Check.Equal(20000, token.Receipt!.ProgressItem!.Amount, "token amount");
        await using (var check = source.CreateCommand("SELECT silver FROM public.guilds WHERE id=$1"))
        {
            check.Parameters.AddWithValue(tokenGuild);
            Check.Equal(20000L, (long)(await check.ExecuteScalarAsync())!, "guild silver grew once");
        }
        await using (var drop = source.CreateCommand("DELETE FROM public.guild_members WHERE character_id=$1"))
        {
            drop.Parameters.AddWithValue(fixture.CharacterId);
            await drop.ExecuteNonQueryAsync();
        }

        // EXP stones go through the player-experience settlement.
        await SeedProgressItemAsync(source, fixture, 4170, 4620, 2);
        var expBefore = await ReadProgressStateAsync(source, fixture.CharacterId);
        var exp = await Executor().ExecuteAsync(SackEnvelope(fixture));
        Check.True(exp.Receipt!.Status == PetDurableReceiptStatus.PlayerExperienceAdded, "EXP stone commits");
        Check.Equal(2000, exp.Receipt!.PlayerExperience!.ExperienceGranted, "EXP stone grants its native amount");
        Check.Equal(expBefore.Stack - 1, (await ReadProgressStateAsync(source, fixture.CharacterId)).Stack, "EXP stone consumed");

        // The EXP Pill runs through the same path.
        await SeedProgressItemAsync(source, fixture, 4174, 5149, 2);
        var pill = await Executor().ExecuteAsync(SackEnvelope(fixture));
        Check.True(pill.Receipt!.Status is PetDurableReceiptStatus.PlayerExperienceAdded or
            PetDurableReceiptStatus.PlayerExperienceMaximumReached, "EXP Pill is handled, not rejected as unsupported");

        // A failed ledger write rolls the balance and the bag back together.
        await SeedProgressItemAsync(source, fixture, 4160, 4610, 2);
        var rollbackBefore = await ReadProgressStateAsync(source, fixture.CharacterId);
        var evidence = await ReadSackEvidenceAsync(source, fixture.CharacterId);
        var operation = SackEnvelope(fixture);
        await using (var trigger = source.CreateCommand("""
            CREATE FUNCTION public.reject_progress_item_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected progress item failure'; END; $$;
            CREATE TRIGGER reject_progress_item_test BEFORE INSERT ON public.character_inventory_ledger
            FOR EACH ROW WHEN (NEW.reason_code='progress_item_consumed') EXECUTE FUNCTION public.reject_progress_item_test();
            """)) await trigger.ExecuteNonQueryAsync();
        try
        {
            var rejected = false;
            try { await Executor().ExecuteAsync(operation); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { rejected = true; }
            Check.True(rejected, "injected ledger fault aborts the transaction");
            Check.Equal(rollbackBefore, await ReadProgressStateAsync(source, fixture.CharacterId), "balance and bag roll back together");
            Check.Equal(evidence, await ReadSackEvidenceAsync(source, fixture.CharacterId), "no partial evidence persists");
        }
        finally
        {
            await using var cleanup = source.CreateCommand("DROP TRIGGER reject_progress_item_test ON public.character_inventory_ledger; DROP FUNCTION public.reject_progress_item_test();");
            await cleanup.ExecuteNonQueryAsync();
        }
        Check.True((await Executor().ExecuteAsync(operation)).IsSuccess, "same operation succeeds after the fault is removed");

        // A gift pack delivers its exact list, splits over stack limits, and refuses without consuming when it cannot fit.
        await SeedProgressItemAsync(source, fixture, 14039, 5548, 2);
        await using (var clear = source.CreateCommand("DELETE FROM character_bag_consumable_cooldowns WHERE character_id=@c;"))
        { clear.Parameters.AddWithValue("c", fixture.CharacterId); await clear.ExecuteNonQueryAsync(); }
        var packEnvelope = SackEnvelope(fixture);
        var opened = await Executor().ExecuteAsync(packEnvelope);
        Check.True(opened.Receipt!.Status == PetDurableReceiptStatus.GiftPackOpened, "gift pack commits: " + opened.Receipt!.Status);
        Check.Equal("4005:200:0", opened.Receipt!.GiftPack!.Contents, "gift pack evidence");
        Check.Equal(200L, await ReadItemCountAsync(source, fixture.CharacterId, 4005), "exact reward quantity across stacks");
        Check.Equal(1L, await ReadItemCountAsync(source, fixture.CharacterId, 14039), "one pack consumed");
        var again = await Executor().ExecuteAsync(packEnvelope);
        Check.True(again.Disposition == PetDurableExecutionDisposition.Duplicate, "pack replay is a duplicate");
        Check.Equal(200L, await ReadItemCountAsync(source, fixture.CharacterId, 4005), "replay grants nothing");
        await using (var fill = source.CreateCommand("""
            DELETE FROM character_items WHERE user_id=@c AND item_location=1;
            DELETE FROM character_bag_consumable_cooldowns WHERE character_id=@c;
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
            SELECT @c,1,s,CASE WHEN s=@slot THEN 14040 ELSE 1001 END,0,1,0,1 FROM generate_series(0,95) s;
            """))
        {
            fill.Parameters.AddWithValue("c", fixture.CharacterId);
            fill.Parameters.AddWithValue("slot", fixture.EggSlot);
            await fill.ExecuteNonQueryAsync();
        }
        var noRoom = await Executor().ExecuteAsync(SackEnvelope(fixture));
        Check.True(noRoom.Receipt!.Status == PetDurableReceiptStatus.GiftPackBagFull, "full bag refuses the pack: " + noRoom.Receipt!.Status);
        Check.Equal(1L, await ReadItemCountAsync(source, fixture.CharacterId, 14040), "refused pack not consumed");

        // Every receipt already stored by older binaries must still replay.
        await using var stored = source.CreateCommand("""
            SELECT result_payload::text, result_hash, result_contract_version FROM command_inbox
            WHERE command_family='bag_item_activation' AND result_payload IS NOT NULL
              AND (result_contract_version < 8 OR completed_at >= @runStart);
            """);
        stored.Parameters.AddWithValue("runStart", runStart);
        await using var rows = await stored.ExecuteReaderAsync();
        var verified = new Dictionary<short, int>();
        while (await rows.ReadAsync())
        {
            _ = PetDurablePersistenceCodec.DecodeAndVerify(rows.GetString(0), rows.GetFieldValue<byte[]>(1));
            var version = rows.GetInt16(2);
            verified[version] = verified.GetValueOrDefault(version) + 1;
        }
        Check.True(verified.GetValueOrDefault((short)8) > 0, "the run itself stored v8 receipts");
        Console.WriteLine("stored receipts verified by version: " + string.Join(", ", verified.OrderBy(p => p.Key).Select(p => $"v{p.Key}={p.Value}")));
    }

    private static async Task SeedProgressItemAsync(NpgsqlDataSource source, PetFixture fixture, int item, int skill, int stack)
    {
        await using var command = source.CreateCommand("""
            DELETE FROM character_items WHERE user_id=@character AND item_location=1;
            DELETE FROM character_bag_consumable_cooldowns WHERE character_id=@character AND cooldown_group=@skill;
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
            VALUES(@character,1,@slot,@item,0,10,0,@stack);
            """);
        command.Parameters.AddWithValue("character", fixture.CharacterId);
        command.Parameters.AddWithValue("skill", skill);
        command.Parameters.AddWithValue("slot", checked((short)fixture.EggSlot));
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("stack", checked((short)stack));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadItemCountAsync(NpgsqlDataSource source, int character, int item)
    {
        await using var command = source.CreateCommand("SELECT COALESCE(sum(stack),0) FROM character_items WHERE user_id=@c AND item_location=1 AND prop_id=@i;");
        command.Parameters.AddWithValue("c", character);
        command.Parameters.AddWithValue("i", item);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task SetBalanceAsync(NpgsqlDataSource source, int character, string column, int value)
    {
        await using var command = source.CreateCommand($"UPDATE character_base SET {column}=@value WHERE id=@character;");
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("character", character);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ProgressState> ReadProgressStateAsync(NpgsqlDataSource source, int character)
    {
        await using var command = source.CreateCommand("""
            SELECT "Money","Stone","BindingGold",medusa_honor_points,exchange_point,"SkillPoint","curHP",
            COALESCE((SELECT sum(stack) FROM character_items WHERE user_id=@character AND item_location=1),0),
            (SELECT count(*) FROM character_currency_ledger WHERE character_id=@character)
            FROM character_base WHERE id=@character;
            """);
        command.Parameters.AddWithValue("character", character);
        await using var r = await command.ExecuteReaderAsync();
        Check.True(await r.ReadAsync(), "progress state exists");
        return new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt64(7), r.GetInt64(8));
    }

    private sealed record ProgressState(int Silver, int Gold, int BindingGold, int Honor, int Point, int Talent, int Hp, long Stack, long Ledger);
}
