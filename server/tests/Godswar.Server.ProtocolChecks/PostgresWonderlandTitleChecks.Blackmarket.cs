using System.Text.RegularExpressions;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWonderlandTitleChecks
{
    public const string BlackmarketCheckName =
        "PostgreSQL Wonderland paid transport charges and compensates once with ownership and currency evidence";

    public static async Task RunBlackmarketAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("GODSWAR_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException(BlackmarketCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, @"^godswar_b(?:03|08|09|10|11|12)_[a-z0-9_]{1,48}$",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                throw new CheckSkippedException(BlackmarketCheckName + " requires a disposable database");
        }
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var game = new PostgresGameStore(connectionString);
        await game.EnsureSeedDataAsync();
        await CheckBlackmarketChargesAsync(source);
        await CheckBlackmarketFirstIslandAsync(source);
        await CheckBlackmarketRefusalsAsync(source);
        await CheckBlackmarketOwnerCompensationAsync(source);
        await CheckBlackmarketRollbackAsync(source);
    }

    private static async Task CheckBlackmarketChargesAsync(NpgsqlDataSource source)
    {
        foreach (var (function, cost) in new[] { (59, 5000), (62, 6000), (63, 8000) })
        {
            var run = Copy(await CreateAsync(source, count: 1), island: 1);
            Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(run)).Succeeded,
                "the paid transport fixture durably clears its source island");
            var request = BlackmarketRequest(run, function);
            Check.Equal(cost, request.Cost, "native service prices remain exactly 5000, 6000 and 8000 Silver");
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                new PostgresWonderlandBlackmarketStore(source).ChargeAsync(request, default)));
            var charged = new WonderlandBlackmarketReceipt(WonderlandBlackmarketStatus.Committed, 10000 - cost, 1);
            Check.True(results.All(result => result == charged),
                "concurrent same-operation requests return one frozen debit receipt");
            await AssertBlackmarketWalletAsync(source, request, 10000 - cost, 1,
                $"1:silver:-{cost}:10000:{10000 - cost}:wonderland_blackmarket_charge");
            var committed = await BlackmarketStateAsync(source, request);
            Check.Equal(charged, await new PostgresWonderlandBlackmarketStore(source).ChargeAsync(request, default),
                "a restarted store returns the original charge without a second debit");
            Check.Equal(committed, await BlackmarketStateAsync(source, request),
                "charge replay preserves wallet, inventory and all receipt evidence");
            foreach (var conflict in new[] { request with { Function = function == 59 ? 62 : 59 },
                request with { Instance = WorldInstanceId.New() }, request with { TargetIsland = 3 } })
            {
                var rejected = false;
                try { await new PostgresWonderlandBlackmarketStore(source).ChargeAsync(conflict, default); }
                catch (InvalidDataException) { rejected = true; }
                Check.True(rejected, "same operation cannot replace its frozen price, instance or destination");
                Check.Equal(committed, await BlackmarketStateAsync(source, request),
                    "conflicting operation identity does not alter any committed evidence");
            }

            var refunds = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                new PostgresWonderlandBlackmarketStore(source).RefundAsync(request, default)));
            var refunded = new WonderlandBlackmarketReceipt(WonderlandBlackmarketStatus.Committed, 10000, 2);
            Check.True(refunds.All(result => result == refunded),
                "concurrent relocation compensation refunds the exact debit once");
            await AssertBlackmarketWalletAsync(source, request, 10000, 2,
                $"1:silver:-{cost}:10000:{10000 - cost}:wonderland_blackmarket_charge," +
                $"2:silver:{cost}:{10000 - cost}:10000:wonderland_blackmarket_refund");
            var compensated = await BlackmarketStateAsync(source, request);
            Check.Equal(refunded, await new PostgresWonderlandBlackmarketStore(source).RefundAsync(request, default),
                "refund survives store restart without increasing Silver again");
            Check.True((await new PostgresWonderlandBlackmarketStore(source).ChargeAsync(request, default)).Status ==
                WonderlandBlackmarketStatus.NotEligible,
                "a refunded operation is terminal and cannot authorize a free relocation through its old charge receipt");
            Check.Equal(compensated, await BlackmarketStateAsync(source, request),
                "refund replay and post-refund charge preserve exact wallet and ledger state");
        }
    }

    private static async Task CheckBlackmarketRefusalsAsync(NpgsqlDataSource source)
    {
        var run = Copy(await CreateAsync(source, count: 1), island: 1);
        var request = BlackmarketRequest(run);
        var store = new PostgresWonderlandBlackmarketStore(source);
        var before = await BlackmarketStateAsync(source, request);
        Check.True((await store.ChargeAsync(request, default)).Status == WonderlandBlackmarketStatus.NotEligible,
            "admission alone cannot pay past an island whose completion has not settled");
        Check.True((await store.RefundAsync(request, default)).Status == WonderlandBlackmarketStatus.NotEligible,
            "a never-charged operation cannot mint a refund");
        Check.Equal(before, await BlackmarketStateAsync(source, request), "unearned transport preserves wallet and evidence");
        Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(run)).Succeeded,
            "rejection fixture's source island completion settles");
        before = await BlackmarketStateAsync(source, request);
        foreach (var invalid in new[] { request with { Realm = RealmId.Dwargon },
            request with { Instance = WorldInstanceId.New() }, request with { TargetIsland = 3 },
            request with { TargetIsland = 0 }, request with { TargetIsland = 9 },
            request with { Function = 58 }, request with { OperationId = Guid.Empty } })
            Check.True((await store.ChargeAsync(invalid, default)).Status == WonderlandBlackmarketStatus.NotEligible,
                "wrong realm, unrelated run, uncleared source island and malformed native service cannot debit");
        Check.True((await store.ChargeAsync(request with {
            Ownership = new PlayerOwnershipFence(Guid.NewGuid(), request.Ownership.Generation) }, default)).Status ==
            WonderlandBlackmarketStatus.OwnershipLost, "a stale session cannot charge Silver");
        Check.Equal(before, await BlackmarketStateAsync(source, request),
            "all eligibility and ownership failures preserve the wallet and durable evidence");
        await MutateAsync(source, run, "UPDATE character_base SET \"Money\"=4999 WHERE id=@character;");
        before = await BlackmarketStateAsync(source, request);
        Check.True((await store.ChargeAsync(request, default)).Status == WonderlandBlackmarketStatus.InsufficientSilver,
            "one Silver below the fee cannot charge, partially pay, or create a committed receipt");
        Check.Equal(before, await BlackmarketStateAsync(source, request),
            "insufficient funds leave wallet revision and every evidence table unchanged");
    }

    private static async Task CheckBlackmarketOwnerCompensationAsync(NpgsqlDataSource source)
    {
        var run = Copy(await CreateAsync(source, count: 1), island: 1);
        Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(run)).Succeeded,
            "ownership-race fixture clears its source island");
        var request = BlackmarketRequest(run);
        var store = new PostgresWonderlandBlackmarketStore(source);
        Check.True((await store.ChargeAsync(request, default)).Succeeded, "ownership-race fee first commits");
        await MutateAsync(source, run, """
            UPDATE character_base SET checkpoint_owner_id=NULL,checkpoint_owner_generation=checkpoint_owner_generation+1
            WHERE id=@character;
            """);
        var before = await BlackmarketStateAsync(source, request);
        Check.True((await store.ChargeAsync(request, default)).Status == WonderlandBlackmarketStatus.OwnershipLost,
            "old-owner charge replay cannot authorize transport after session replacement");
        Check.Equal(before, await BlackmarketStateAsync(source, request), "owner rejection does not mutate the existing debit");
        Check.True((await store.RefundAsync(request, default)).Succeeded,
            "exact compensation may restore committed Silver even when relocation loses ownership");
        await AssertBlackmarketWalletAsync(source, request, 10000, 2,
            "1:silver:-5000:10000:5000:wonderland_blackmarket_charge," +
            "2:silver:5000:5000:10000:wonderland_blackmarket_refund");
        before = await BlackmarketStateAsync(source, request);
        Check.True((await new PostgresWonderlandBlackmarketStore(source).RefundAsync(request, default)).Succeeded,
            "a restarted compensation worker can replay the exact original-owner refund");
        Check.True((await store.RefundAsync(request with { OperationId = Guid.NewGuid() }, default)).Status ==
            WonderlandBlackmarketStatus.NotEligible, "losing ownership never permits an unrelated refund");
        var conflict = false;
        try { await store.RefundAsync(request with { Function = 63 }, default); }
        catch (InvalidDataException) { conflict = true; }
        Check.True(conflict, "compensation cannot increase its amount by reusing an operation with a different fee");
        Check.Equal(before, await BlackmarketStateAsync(source, request),
            "repeated and forged refunds preserve the compensated balance and evidence");
    }

    private static async Task CheckBlackmarketRollbackAsync(NpgsqlDataSource source)
    {
        var run = Copy(await CreateAsync(source, count: 1), island: 1);
        Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(run)).Succeeded,
            "rollback fixture clears its source island");
        var request = BlackmarketRequest(run);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var function = "wonder_blackmarket_fail_" + suffix;
        var trigger = "wonder_blackmarket_trigger_" + suffix;
        foreach (var refund in new[] { false, true })
        {
            var before = await BlackmarketStateAsync(source, request);
            await using (var install = source.CreateCommand($"""
                CREATE FUNCTION public.{function}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW.character_id={request.Subject.CharacterId} THEN
                        RAISE EXCEPTION 'Wonderland blackmarket ledger rollback fixture' USING ERRCODE='P0001';
                    END IF;
                    RETURN NEW;
                END; $body$;
                CREATE TRIGGER {trigger} BEFORE INSERT ON public.character_currency_ledger
                    FOR EACH ROW EXECUTE FUNCTION public.{function}();
                """)) await install.ExecuteNonQueryAsync();
            try
            {
                var rejected = false;
                var store = new PostgresWonderlandBlackmarketStore(source);
                try
                {
                    if (refund) await store.RefundAsync(request, default);
                    else await store.ChargeAsync(request, default);
                }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.RaiseException) { rejected = true; }
                Check.True(rejected, "the injected currency-ledger failure reaches the valuable mutation transaction");
                Check.Equal(before, await BlackmarketStateAsync(source, request),
                    "failed charge or refund rolls back wallet, baseline, revision, inbox, audit and ledger together");
            }
            finally
            {
                await using var cleanup = source.CreateCommand($"""
                    DROP TRIGGER IF EXISTS {trigger} ON public.character_currency_ledger;
                    DROP FUNCTION IF EXISTS public.{function}();
                    """);
                await cleanup.ExecuteNonQueryAsync();
            }
            var retry = new PostgresWonderlandBlackmarketStore(source);
            var result = refund ? await retry.RefundAsync(request, default) : await retry.ChargeAsync(request, default);
            Check.True(result.Succeeded && result.WalletRevision == (refund ? 2 : 1),
                "the unchanged operation retries after rollback and commits only one wallet revision");
        }
        await AssertBlackmarketWalletAsync(source, request, 10000, 2,
            "1:silver:-5000:10000:5000:wonderland_blackmarket_charge," +
            "2:silver:5000:5000:10000:wonderland_blackmarket_refund");
    }

    private static WonderlandBlackmarketRequest BlackmarketRequest(WonderlandTitleRequest run, int function = 59)
    {
        var member = run.FrozenMembers[0];
        return new(Guid.NewGuid(), new(member.AccountId, member.CharacterId), member.Ownership,
            run.RealmId, run.WorldInstanceId, run.IslandNumber + 1, function, run.AdmissionReservationId);
    }

    private static async Task<string> BlackmarketStateAsync(NpgsqlDataSource source, WonderlandBlackmarketRequest request)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_build_object('character',to_jsonb(c),
                'items',(SELECT jsonb_agg(to_jsonb(i) ORDER BY id) FROM character_items i WHERE user_id=c.id),
                'baseline',(SELECT to_jsonb(b) FROM character_economy_baseline b WHERE character_id=c.id),
                'ledger',(SELECT jsonb_agg(to_jsonb(l) ORDER BY id) FROM character_currency_ledger l WHERE character_id=c.id),
                'inbox',(SELECT jsonb_agg(to_jsonb(i) ORDER BY id) FROM command_inbox i
                    WHERE aggregate_key='character:'||c.id::text),
                'audit',(SELECT jsonb_agg(to_jsonb(a) ORDER BY id) FROM command_audit a
                    WHERE aggregate_key='character:'||c.id::text))::text
            FROM character_base c WHERE id=@character;
            """);
        command.Parameters.AddWithValue("character", request.Subject.CharacterId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertBlackmarketWalletAsync(NpgsqlDataSource source, WonderlandBlackmarketRequest request,
        int silver, long revision, string ledger)
    {
        await using var command = source.CreateCommand("""
            SELECT c."Money",c.wallet_revision,c."Stone",c."BindingGold",c.fighter_job_exp,c.inventory_revision,
                (SELECT string_agg(l.wallet_revision::text||':'||l.currency_code||':'||l.delta::text||':'||
                    l.balance_before::text||':'||l.balance_after::text||':'||l.reason_code,',' ORDER BY l.wallet_revision)
                 FROM character_currency_ledger l WHERE l.character_id=c.id),
                (SELECT count(*) FROM command_inbox WHERE aggregate_key='character:'||c.id::text
                    AND command_family IN ('wonderland_blackmarket_charge','wonderland_blackmarket_refund')),
                (SELECT count(*) FROM command_audit WHERE aggregate_key='character:'||c.id::text
                    AND command_family IN ('wonderland_blackmarket_charge','wonderland_blackmarket_refund')),
                (SELECT silver FROM character_economy_baseline WHERE character_id=c.id),
                (SELECT count(*) FROM character_items WHERE user_id=c.id)
            FROM character_base c WHERE c.id=@character;
            """);
        command.Parameters.AddWithValue("character", request.Subject.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "paid traveler remains persisted");
        Check.Equal(silver, reader.GetInt32(0), "exact paid transport Silver balance");
        Check.Equal(revision, reader.GetInt64(1), "one wallet revision per committed charge or refund");
        Check.Equal(10, reader.GetInt32(2), "paid transport preserves Gold");
        Check.Equal(0, reader.GetInt32(3), "paid transport preserves Bound Gold");
        Check.Equal(0L, reader.GetInt64(4), "paid transport preserves EXP");
        Check.Equal(0L, reader.GetInt64(5), "paid transport preserves inventory revision");
        Check.Equal(ledger, reader.IsDBNull(6) ? "<none>" : reader.GetString(6),
            "ledger records exact debit/credit, currency, before/after balances, reason and contiguous revisions");
        Check.Equal(revision, reader.GetInt64(7), "each committed currency mutation has one durable inbox receipt");
        Check.Equal(revision, reader.GetInt64(8), "each committed currency mutation has one audit entry");
        Check.True(!reader.IsDBNull(9), "first paid transport creates its economy baseline before the currency ledger");
        Check.Equal(10000L, reader.GetInt64(9), "economy baseline preserves the pre-debit Silver balance");
        Check.Equal(0L, reader.GetInt64(10), "transport neither grants nor consumes inventory items");
    }
}
