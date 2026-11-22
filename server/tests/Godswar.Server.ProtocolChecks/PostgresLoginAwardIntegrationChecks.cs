using Godswar.Server.Application.Awards;
using Godswar.Server.Infrastructure.Awards;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// W4-B against a real PostgreSQL (GODSWAR_TEST_POSTGRES_CONNECTION_STRING): migration 240 applies, an award is
/// claimed once per character or per account, a full bag sends the items to the Prize Chest, the chest hands over
/// what fits and keeps the rest, queued buffs are claimed once.
/// </summary>
internal static class PostgresLoginAwardIntegrationChecks
{
    public const string CheckName = "W4-B PostgreSQL login awards, Prize Chest and queued buffs";
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var source = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        var store = new PostgresEventsNpcStore(source);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid().ToString("N")[..10];

        var stackable = await ScalarIntAsync(source,
            "SELECT id FROM public.item_templates WHERE stats->>'Overlap' = '99' ORDER BY id LIMIT 1;");
        var single = await ScalarIntAsync(source,
            "SELECT id FROM public.item_templates WHERE stats->>'Overlap' = '1' ORDER BY id LIMIT 1;");
        var titleId = await ScalarIntAsync(source, "SELECT min(title_id) FROM public.editor_title_catalog;");
        var accountId = await ScalarIntAsync(source,
            $"INSERT INTO public.accounts(username,password) VALUES('w4b_{token}','') RETURNING id;");
        var first = await CreateCharacterAsync(source, accountId, $"W4bA{token}", 40, 1);
        var second = await CreateCharacterAsync(source, accountId, $"W4bB{token}", 40, 2);
        var awards = new List<int>();
        try
        {
            // Per-character award: once each, a second login gets nothing.
            var perCharacter = await CreateAwardAsync(source, awards, token + "c", now, "character", "bag",
                $$"""[{"item_id":{{stackable}},"quantity":5}]""", silver: 1000, title: titleId,
                buffs: """[{"status_id":1000,"duration_seconds":120}]""");
            var grants = await store.RunAsync(first, day,
                tx => LoginAwardLedger.GrantOpenAwardsAsync(tx, now, accountId, first, 40), CancellationToken.None);
            var mine = grants.Where(g => g.Award.Id == perCharacter).ToArray();
            Check.True(mine.Length == 1 && !mine[0].ToPrizeChest && mine[0].BagItems.Single() == (stackable, 5) &&
                mine[0].TitleGranted && mine[0].BuffsQueued == 1, "first login pays the award into the bag");
            Check.Equal(5L, await ScalarLongAsync(source,
                $"SELECT coalesce(sum(stack),0) FROM public.character_items WHERE user_id={first} AND item_location=1 AND prop_id={stackable};"),
                "five of the item are in the bag");
            Check.Equal(1L, await ScalarLongAsync(source,
                $"SELECT count(*) FROM public.character_title_grants WHERE character_id={first} AND title_id={titleId};"),
                "the title is granted");
            var again = await store.RunAsync(first, day,
                tx => LoginAwardLedger.GrantOpenAwardsAsync(tx, now, accountId, first, 40), CancellationToken.None);
            Check.True(again.All(g => g.Award.Id != perCharacter), "a second login of the same character gets nothing");
            var other = await store.RunAsync(second, day,
                tx => LoginAwardLedger.GrantOpenAwardsAsync(tx, now, accountId, second, 40), CancellationToken.None);
            Check.True(other.Any(g => g.Award.Id == perCharacter), "the account's other character gets its own copy");

            // Per-account award: only the first character of the account.
            var perAccount = await CreateAwardAsync(source, awards, token + "a", now, "account", "prize_chest",
                $$"""[{"item_id":{{single}},"quantity":2,"quality":8,"grade":3,"bound":0}]""", bindingGold: 300);
            var accountFirst = await store.RunAsync(second, day,
                tx => LoginAwardLedger.GrantOpenAwardsAsync(tx, now, accountId, second, 40), CancellationToken.None);
            var accountSecond = await store.RunAsync(first, day,
                tx => LoginAwardLedger.GrantOpenAwardsAsync(tx, now, accountId, first, 40), CancellationToken.None);
            Check.True(accountFirst.Any(g => g.Award.Id == perAccount && g.ToPrizeChest) &&
                accountSecond.All(g => g.Award.Id != perAccount), "a per-account award is paid to one character only");
            Check.Equal(2L, await ScalarLongAsync(source,
                $"SELECT count(*) FROM public.prize_chest_entries WHERE character_id={second} AND claimed_at IS NULL;"),
                "chest delivery: one item row and one B-Gold row");

            // Below min_level, outside the window, disabled: nothing.
            var highLevel = await CreateAwardAsync(source, awards, token + "h", now, "character", "bag",
                $$"""[{"item_id":{{stackable}},"quantity":1}]""", minLevel: 41);
            var low = await store.RunAsync(first, day,
                tx => LoginAwardLedger.GrantOpenAwardsAsync(tx, now, accountId, first, 40), CancellationToken.None);
            Check.True(low.All(g => g.Award.Id != highLevel), "level 40 does not qualify for min_level 41");
            Check.Equal(0L, await ScalarLongAsync(source,
                $"SELECT count(*) FROM public.login_window_award_claims WHERE award_id={highLevel};"),
                "an ineligible character leaves no claim behind");

            // Full bag: the items go to the chest, the claim is not lost.
            await FillBagAsync(source, first, single);
            var fullBag = await CreateAwardAsync(source, awards, token + "f", now, "character", "bag",
                $$"""[{"item_id":{{single}},"quantity":1}]""", silver: 50);
            var full = await store.RunAsync(first, day,
                tx => LoginAwardLedger.GrantOpenAwardsAsync(tx, now, accountId, first, 40), CancellationToken.None);
            var fallback = full.Single(g => g.Award.Id == fullBag);
            Check.True(fallback.ToPrizeChest && fallback.BagWasFull && fallback.BagItems.Count == 0,
                "a full bag sends the award's items to the Prize Chest");
            Check.Equal("prize_chest", await ScalarTextAsync(source,
                $"SELECT delivered_to FROM public.login_window_award_claims WHERE award_id={fullBag};"), "claim says where it went");
            Check.Equal(96L, await ScalarLongAsync(source,
                $"SELECT count(*) FROM public.character_items WHERE user_id={first} AND item_location=1;"),
                "the rolled-back attempt left the bag exactly full");

            // The chest keeps what does not fit, then hands it over once there is room.
            var blocked = await store.RunAsync(first, day, tx => LoginAwardLedger.DrawPrizeChestAsync(tx), CancellationToken.None);
            Check.True(blocked.Items.Count == 0 && blocked.Waiting == 1, "a full bag draws nothing and keeps the prize");
            await source.CreateCommand($"DELETE FROM public.character_items WHERE user_id={first} AND item_location=1 AND slot_index >= 90;")
                .ExecuteNonQueryAsync();
            var drawn = await store.RunAsync(first, day, tx => LoginAwardLedger.DrawPrizeChestAsync(tx), CancellationToken.None);
            Check.True(drawn.Items.Single() == (single, 1) && drawn.Waiting == 0, "with room the prize moves to the bag");
            Check.Equal(0L, await ScalarLongAsync(source,
                $"SELECT count(*) FROM public.prize_chest_entries WHERE character_id={first} AND claimed_at IS NULL;"),
                "nothing waits after the draw");

            var chest = await store.RunAsync(second, day, LoginAwardLedger.ReadPrizeChestAsync, CancellationToken.None);
            var beforeBGold = await ScalarLongAsync(source, $"SELECT \"BindingGold\" FROM public.character_base WHERE id={second};");
            var paid = await store.RunAsync(second, day, tx => LoginAwardLedger.DrawPrizeChestAsync(tx), CancellationToken.None);
            Check.True(chest.Count == 2 && paid.BindingGold == 300 && paid.Items.Single() == (single, 2), "per-account chest draw");
            Check.Equal(beforeBGold + 300, await ScalarLongAsync(source,
                $"SELECT \"BindingGold\" FROM public.character_base WHERE id={second};"), "B-Gold is credited");
            Check.Equal(2L, await ScalarLongAsync(source,
                $"SELECT count(*) FROM public.character_items WHERE user_id={second} AND item_location=1 AND prop_id={single} AND item_quality=8 AND item_grade=3 AND bound=0;"),
                "a non-stacking item keeps quality, grade and binding, one row each");

            // Queued buffs: claimed once; expired ones discarded.
            await source.CreateCommand($"""
                INSERT INTO public.pending_character_buffs (character_id,status_id,duration_seconds,source,created_by,discard_after)
                VALUES ({second},1001,60,'check','check',NULL),({second},1002,NULL,'check','check',now() - interval '1 minute');
                """).ExecuteNonQueryAsync();
            var claimed = await store.RunAsync(second, day, LoginAwardLedger.ClaimPendingBuffsAsync, CancellationToken.None);
            // The second character also has the per-character award's buff (status 1000) waiting.
            Check.True(claimed.Count == 2 && claimed.Any(b => b.StatusId == 1001 && b.DurationSeconds == 60) &&
                claimed.Any(b => b.StatusId == 1000) && claimed.All(b => b.StatusId != 1002),
                "waiting buffs are claimed with their durations, oldest first, the expired one not");
            Check.Equal(0, (await store.RunAsync(second, day, LoginAwardLedger.ClaimPendingBuffsAsync, CancellationToken.None)).Count,
                "a claimed buff is not handed out twice");
            Check.Equal("discarded: not back in time", await ScalarTextAsync(source,
                $"SELECT result FROM public.pending_character_buffs WHERE character_id={second} AND status_id=1002;"),
                "an expired queued buff is closed, not applied");
            var firstBuffs = await store.RunAsync(first, day, LoginAwardLedger.ClaimPendingBuffsAsync, CancellationToken.None);
            Check.True(firstBuffs.Any(b => b.StatusId == 1000 && b.DurationSeconds == 120), "the award's buff was queued");

            // Database guards.
            await ExpectFailureAsync(source, $"""
                INSERT INTO public.prize_chest_entries (character_id,kind,item_id,quantity,source)
                VALUES ({first},'item',{stackable},100,'check');
                """, "a chest row above 99 is refused");
            await ExpectFailureAsync(source, $"""
                INSERT INTO public.login_window_awards (name,starts_at,ends_at,created_by)
                VALUES ('bad', now(), now() - interval '1 hour', 'check');
                """, "an award that ends before it starts is refused");
        }
        finally
        {
            await using var cleanup = source.CreateCommand(
                $"DELETE FROM public.login_window_awards WHERE id = ANY(@ids); DELETE FROM public.accounts WHERE id = {accountId};");
            cleanup.Parameters.AddWithValue("ids", awards.ToArray());
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> CreateCharacterAsync(NpgsqlDataSource source, int accountId, string name, int level, int realm)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO public.character_base(account_id,server_id,name,gender,camp,profession,fighter_job_lv,
                fighter_job_exp,"SkillExp","SkillPoint",belief,"Map","Pos_X","Pos_Z","MaxHP","curHP","MaxMP","curMP",character_slot)
            VALUES(@a,@realm,@n,'male',1,0,@l,0,0,10,1,1,165,-97,1500,1500,177,177,0) RETURNING id;
            """);
        command.Parameters.AddWithValue("a", accountId);
        command.Parameters.AddWithValue("n", name);
        command.Parameters.AddWithValue("l", level);
        command.Parameters.AddWithValue("realm", realm); // one character per account and realm: the two are on realms 1 and 2
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CreateAwardAsync(NpgsqlDataSource source, List<int> created, string name,
        DateTimeOffset now, string scope, string delivery, string items, int silver = 0, int bindingGold = 0,
        int? title = null, string buffs = "[]", int minLevel = 1)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO public.login_window_awards (name,starts_at,ends_at,per_scope,delivery,items,silver,binding_gold,
                buffs,title_id,min_level,created_by)
            VALUES (@n,@s,@e,@scope,@d,@items::jsonb,@silver,@bg,@buffs::jsonb,@t,@min,'check') RETURNING id;
            """);
        command.Parameters.AddWithValue("n", name);
        command.Parameters.AddWithValue("s", now.AddHours(-1).UtcDateTime);
        command.Parameters.AddWithValue("e", now.AddHours(1).UtcDateTime);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("d", delivery);
        command.Parameters.AddWithValue("items", items);
        command.Parameters.AddWithValue("silver", silver);
        command.Parameters.AddWithValue("bg", bindingGold);
        command.Parameters.AddWithValue("buffs", buffs);
        command.Parameters.AddWithValue("t", title is { } id ? id : DBNull.Value);
        command.Parameters.AddWithValue("min", (short)minLevel);
        var awardId = Convert.ToInt32(await command.ExecuteScalarAsync());
        created.Add(awardId);
        return awardId;
    }

    private static async Task FillBagAsync(NpgsqlDataSource source, int characterId, int itemId)
    {
        await using var command = source.CreateCommand($"""
            INSERT INTO public.character_items (user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack,item_exp,holy_suit_code)
            SELECT {characterId},1,s,{itemId},1,1,1,1,0,0 FROM generate_series(0,95) s
            WHERE NOT EXISTS (SELECT 1 FROM public.character_items i WHERE i.user_id={characterId} AND i.item_location=1 AND i.slot_index=s);
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExpectFailureAsync(NpgsqlDataSource source, string sql, string label)
    {
        try
        {
            await source.CreateCommand(sql).ExecuteNonQueryAsync();
        }
        catch (PostgresException)
        {
            return;
        }

        throw new InvalidOperationException("expected a database refusal: " + label);
    }

    private static async Task<int> ScalarIntAsync(NpgsqlDataSource source, string sql) =>
        Convert.ToInt32(await source.CreateCommand(sql).ExecuteScalarAsync() ??
            throw new InvalidOperationException("fixture query returned nothing: " + sql));

    private static async Task<long> ScalarLongAsync(NpgsqlDataSource source, string sql) =>
        Convert.ToInt64(await source.CreateCommand(sql).ExecuteScalarAsync());

    private static async Task<string?> ScalarTextAsync(NpgsqlDataSource source, string sql) =>
        (string?)await source.CreateCommand(sql).ExecuteScalarAsync();
}
