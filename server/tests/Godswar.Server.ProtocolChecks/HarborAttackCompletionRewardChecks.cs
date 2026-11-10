using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// BAY2: Bay Under Attack completion EXP scaled by clearance (generated curve),
/// the frozen request, the level band, harbor gear drops at quantity 1, and -
/// against a PRIVATE rig database only - the exactly-once Postgres settlement
/// (sequential and concurrent double claims, conflicts, level 200, unadmitted
/// members, and the restart refund never refunding a paid run).
/// </summary>
internal static class HarborAttackCompletionRewardChecks
{
    public const string CheckName =
        "Bay Under Attack completion EXP by clearance, level band, gear drops (BAY2)";

    public const string PostgresCheckName =
        "Bay Under Attack completion EXP exactly-once settlement, PostgreSQL (BAY2)";

    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        CheckPolicy();
        CheckRequest();
        CheckLevelBandAndRates();
        CheckGearQuantity();
        await Task.CompletedTask;
    }

    public static Task RunPostgresCheckAsync() => RunPostgresAsync();

    private static void CheckPolicy()
    {
        Check.Equal(2_300, HarborAttackCompletionRewardPolicy.ClearanceBasisPoints(52, 226),
            "52 of 226 points is 23.00% (floor)");
        Check.Equal(5_000, HarborAttackCompletionRewardPolicy.ClearanceBasisPoints(113, 226), "half");
        Check.Equal(10_000, HarborAttackCompletionRewardPolicy.ClearanceBasisPoints(226, 226), "all");
        Check.Equal(10_000, HarborAttackCompletionRewardPolicy.ClearanceBasisPoints(300, 226), "never above 100%");
        Check.Equal(0, HarborAttackCompletionRewardPolicy.ClearanceBasisPoints(5, 0), "an empty map pays nothing");
        Check.Equal(0, HarborAttackCompletionRewardPolicy.ClearanceBasisPoints(0, 226), "nothing cleared pays nothing");

        Check.Equal(44_785, HarborAttackCompletionRewardPolicy.Resolve(55, false, 2_300, 100),
            "level 55 at 23% gets 23% of 194,720");
        Check.Equal(194_720, HarborAttackCompletionRewardPolicy.Resolve(55, false, 10_000, 100),
            "a full clear is one level at the member's level");
        Check.Equal(PlayerExperienceCatalog.GetNextLevelExperience(90),
            HarborAttackCompletionRewardPolicy.Resolve(90, false, 10_000, 100), "level 90 full clear");
        Check.Equal(389_440, HarborAttackCompletionRewardPolicy.Resolve(55, false, 10_000, 200),
            "the percent slider scales it");
        Check.Equal(0, HarborAttackCompletionRewardPolicy.Resolve(200, false, 10_000, 100),
            "level 200 earns no EXP");
        Check.Equal(0, HarborAttackCompletionRewardPolicy.Resolve(200, true, 10_000, 100),
            "level 200 earns no EXP even when sealed");
        Check.Equal(0, HarborAttackCompletionRewardPolicy.Resolve(55, false, 10_000, 0), "slider 0 pays nothing");
        Check.Equal(1, HarborAttackCompletionRewardPolicy.Resolve(1, false, 1, 1), "a positive share pays at least 1");
        Check.Equal(
            HarborAttackCompletionRewardPolicy.Resolve(55, false, 10_000, 1_000),
            HarborAttackCompletionRewardPolicy.Resolve(55, false, 10_000, 5_000),
            "the slider is capped at 1,000%");
    }

    private static HarborAttackCompletionRewardRequest Request(Guid run, Guid instance, int cleared,
        string reason = "completed", HarborAttackCompletionMember[]? members = null, Guid[]? late = null) =>
        new(run, instance, 1, 208, reason,
            new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 3, 10, 20, 0, TimeSpan.Zero),
            cleared, 226, 100,
            members ?? [new(46, 14), new(8, 13), new(44, 10)],
            late ?? []);

    private static void CheckRequest()
    {
        var run = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var a = Request(run, instance, 52);
        var b = Request(run, instance, 52, members: [new(44, 10), new(46, 14), new(8, 13)]);
        Check.Equal(a.RequestHash, b.RequestHash, "the hash does not depend on the member order");
        Check.True(a.CharacterIds.SequenceEqual([10, 13, 14]), "members are held in character order");
        Check.True(a.RequestHash != Request(run, instance, 53).RequestHash, "another score is another request");
        Check.True(a.RequestHash != Request(run, instance, 52, "terminated").RequestHash,
            "another end reason is another request");
        Check.True(a.EntryReservationIds.Contains(run), "the run's own reservation is always fenced");
        foreach (var bad in new Func<HarborAttackCompletionRewardRequest>[]
                 {
                     () => Request(run, instance, 52, "abandoned"),
                     () => Request(Guid.Empty, instance, 52),
                     () => Request(run, instance, -1),
                     () => Request(run, instance, 52, members: []),
                     () => Request(run, instance, 52, members: [new(1, 1), new(2, 2), new(3, 3), new(4, 4), new(5, 5), new(6, 6)]),
                     () => Request(run, instance, 52, members: [new(1, 1), new(2, 1)]),
                     () => Request(run, instance, 52, members: [new(1, 1), new(1, 2)])
                 })
        {
            Check.True(Throws(bad), "invalid evidence is refused");
        }
    }

    private static bool Throws(Func<object> create)
    {
        try
        {
            create();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void CheckLevelBandAndRates()
    {
        var low = InstanceCallerProtocol.ResolveHarborAttackDestination(50);
        var high = InstanceCallerProtocol.ResolveHarborAttackDestination(90);
        Check.True(low.MinimumLevel == 50 && low.MaximumLevel == 90 && high.MaximumLevel == 90 &&
            high.TargetMapId == 209, "Bay Under Attack is joinable by level 50-90 (both bands)");
        var definition = ServerRateCatalog.Definitions.Single(static d =>
            d.Key == ServerRateKeys.HarborAttackExperiencePercent);
        Check.True(definition.DefaultValue == 100m && definition.MinimumValue == 0m && definition.MaximumValue == 1_000m,
            "instance.harbor_attack_exp_pct: 100% by default, 0-1,000");
    }

    private static void CheckGearQuantity()
    {
        var previous = ServerRates.Current;
        try
        {
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(ServerRateKeys.DropQuantity, 2m)], 1, DateTimeOffset.UtcNow, "check", "test"));
            var tables = new[] { new MonsterLootTableRule("BAY2_gear", 3) };
            var rules = new[]
            {
                new MonsterLootRule("BAY2_gear", 0, 1005, 10_000, 1, 1, null, ItemGrantAttributes.None, "harbor_lv060"),
                new MonsterLootRule("BAY2_gear", 1, 1005, 10_000, 1, 1, null, ItemGrantAttributes.None, "elite_lv001"),
                new MonsterLootRule("BAY2_gear", 2, 4200, 10_000, 1, 1)
            };
            var content = new LootRollContent(
                [
                    new LootRollProfile("harbor_lv060", 0, 0, 0, 7_000, 3_000, 1, 2, 5_000, 1, 3, 5_000),
                    new LootRollProfile("elite_lv001", 0, 0, 9_700, 265, 35, 1, 1, 5_000, 1, 2, 5_000)
                ],
                new Dictionary<uint, IReadOnlyList<LootAttributeOption>>
                {
                    [1005] = [new(0, 0), new(40, 4), new(60, 6), new(80, 8), new(110, 11), new(130, 13)]
                });
            var snapshot = new MonsterLootContentSnapshot(tables, rules, content);
            var counts = new int[5];
            for (var index = 1; index <= 2_000; index++)
            {
                var drops = snapshot.RollLoot("BAY2_gear", new Guid(index, 7, 7, 1, 2, 3, 4, 5, 6, 7, 8));
                Check.True(drops.Count == 3 && drops[0].Quantity == 1 && drops[1].Quantity == 2 &&
                    drops[2].Quantity == 2, "drop.quantity 2: harbor_* gear stays one piece, other rules double");
                var attributes = new[] { drops[0].Attributes.Attribute1, drops[0].Attributes.Attribute2,
                    drops[0].Attributes.Attribute3, drops[0].Attributes.Attribute4 }.Count(static a => a is not null);
                counts[attributes]++;
            }
            Check.True(counts[0] == 0 && counts[1] == 0 && counts[2] == 0 && counts[3] > 1_200 && counts[4] > 450,
                $"a harbor gear piece always has 3 or 4 attributes (3:{counts[3]} 4:{counts[4]} of 2,000)");
        }
        finally
        {
            ServerRates.Install(previous);
        }
    }

    // ---- Postgres (PRIVATE rig database only) ------------------------------------------------

    private static async Task RunPostgresAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{PostgresCheckName} ({ConnectionStringVariable} is not set)");
        }
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = Convert.ToString(await dataSource.CreateCommand("SELECT current_database();")
            .ExecuteScalarAsync()) ?? string.Empty;
        if (!database.StartsWith("godswar_bay2", StringComparison.Ordinal))
        {
            throw new CheckSkippedException($"{PostgresCheckName} needs the private bay2 rig database; got '{database}'");
        }

        var ledger = new PostgresHarborAttackRunLedger(dataSource);
        // Four active realm-1 characters of distinct accounts; restored at the end.
        var fixture = new List<(int Account, int Character, int Level, long Experience, long Revision)>();
        await using (var read = dataSource.CreateCommand(
            """
            SELECT DISTINCT ON (account_id) account_id, id, fighter_job_lv, fighter_job_exp, progression_reward_revision
            FROM public.character_base WHERE server_id = 1 AND lifecycle_state = 'active'
              AND fighter_job_lv BETWEEN 2 AND 150
            ORDER BY account_id, id LIMIT 4;
            """))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                fixture.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)));
            }
        }
        Check.Equal(4, fixture.Count, "the rig has four characters to settle with");
        var run = Guid.NewGuid();
        var other = Guid.NewGuid();
        try
        {
            // Members 0-2 hold kind-3 entries (2 on a late reservation); member 3 does not.
            var late = Guid.NewGuid();
            await ExecuteAsync(dataSource,
                "INSERT INTO public.legacy_instance_daily_entries (realm_id, realm_day, instance_kind, character_id, reservation_id, claimed_at, admitted_at) " +
                $"VALUES (1, DATE '2000-01-01', 3, {fixture[0].Character}, '{run}', now(), now()), " +
                $"(1, DATE '2000-01-01', 3, {fixture[1].Character}, '{run}', now(), now()), " +
                $"(1, DATE '2000-01-01', 3, {fixture[2].Character}, '{late}', now(), now());");
            // Member 1 is set to level 200: paid nothing, still a receipt.
            await ExecuteAsync(dataSource, $"UPDATE public.character_base SET fighter_job_lv = 200 WHERE id = {fixture[1].Character};");
            await ExecuteAsync(dataSource,
                "INSERT INTO public.harbor_attack_runs (reservation_id, realm_id, map_id, started_at) " +
                $"VALUES ('{run}', 1, 208, now() - interval '1 hour') ON CONFLICT DO NOTHING;");

            var members = fixture.Select(static f => new HarborAttackCompletionMember(f.Account, f.Character)).ToArray();
            var request = new HarborAttackCompletionRewardRequest(run, Guid.NewGuid(), 1, 208, "terminated",
                DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-40), 113, 226, 100, members, [late]);

            // Three concurrent settlements of the same run: exactly one pays.
            var receipts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => ledger.SettleAsync(request)));
            Check.Equal(1, receipts.Count(static r => r.Status == HarborAttackCompletionRewardStatus.Applied),
                "three concurrent claims: one Applied");
            Check.Equal(2, receipts.Count(static r => r.Status == HarborAttackCompletionRewardStatus.Duplicate),
                "three concurrent claims: two Duplicate");
            var again = await ledger.SettleAsync(request);
            Check.True(again.Status == HarborAttackCompletionRewardStatus.Duplicate, "a later retry is a Duplicate");

            var paid = receipts.Single(static r => r.Status == HarborAttackCompletionRewardStatus.Applied).Members
                .ToDictionary(static m => m.CharacterId);
            var expected0 = HarborAttackCompletionRewardPolicy.Resolve(fixture[0].Level, false, 5_000, 100);
            Check.True(paid[fixture[0].Character].Admitted && paid[fixture[0].Character].ExperienceGranted == expected0,
                $"an admitted member gets 50% of his level ({expected0})");
            Check.True(paid[fixture[1].Character].Admitted && paid[fixture[1].Character].ExperienceGranted == 0,
                "a level-200 member gets a receipt with 0 EXP");
            Check.True(paid[fixture[2].Character].Admitted && paid[fixture[2].Character].ExperienceGranted > 0,
                "a late joiner on his own reservation is admitted and paid");
            Check.True(!paid[fixture[3].Character].Admitted && paid[fixture[3].Character].ExperienceGranted == 0,
                "a member without a Bay Under Attack entry for this run is paid nothing");
            Check.True(again.Members.SequenceEqual(paid.Values.OrderBy(static m => m.CharacterId)),
                "the Duplicate returns the stored receipts");

            var stored = await ReadAsync(dataSource, fixture[0].Character);
            var expectedProgression = PlayerExperienceCatalog.Apply(fixture[0].Level, fixture[0].Experience, expected0);
            Check.True(stored.Level == expectedProgression.Level && stored.Experience == expectedProgression.Experience &&
                stored.Revision == fixture[0].Revision + 1,
                "the EXP was written once, with one progression revision step");
            var level200 = await ReadAsync(dataSource, fixture[1].Character);
            Check.True(level200.Level == 200 && level200.Revision == fixture[1].Revision,
                "the level-200 member's row was not written");

            var conflict = await ledger.SettleAsync(new HarborAttackCompletionRewardRequest(run, request.WorldInstanceId,
                1, 208, "terminated", request.StartedAtUtc, request.EndedAtUtc, 114, 226, 100, members, [late]));
            Check.True(conflict.Status == HarborAttackCompletionRewardStatus.RequestConflict,
                "a different request for a paid run is a conflict and pays nothing");
            Check.Equal(stored, await ReadAsync(dataSource, fixture[0].Character), "the conflict changed nothing");
            Check.Equal("terminated", await ScalarAsync(dataSource,
                $"SELECT close_reason FROM public.harbor_attack_runs WHERE reservation_id = '{run}';"),
                "the payout closed the run's ledger row in the same transaction");

            // A paid run found open at start-up (its close lost) is never refunded.
            await ExecuteAsync(dataSource,
                $"UPDATE public.harbor_attack_runs SET closed_at = NULL, close_reason = NULL WHERE reservation_id = '{run}';");
            var refund = await ledger.RefundInterruptedAsync(1, DateTimeOffset.UtcNow, refundEntries: true);
            Check.True(refund.CompletedRuns >= 1, "the start-up pass counts the paid run as finished");
            Check.Equal("terminated|0", await ScalarAsync(dataSource,
                $"SELECT close_reason || '|' || refunded_entries FROM public.harbor_attack_runs WHERE reservation_id = '{run}';"),
                "the paid run is closed with its own end reason, nothing refunded");
            Check.Equal("3", await ScalarAsync(dataSource,
                $"SELECT count(*) FROM public.legacy_instance_daily_entries WHERE reservation_id IN ('{run}', '{late}');"),
                "its daily entries are kept");

            // An unpaid interrupted run in the same pass IS refunded (the switch still works).
            await ExecuteAsync(dataSource,
                "INSERT INTO public.harbor_attack_runs (reservation_id, realm_id, map_id, started_at) " +
                $"VALUES ('{other}', 1, 208, now() - interval '1 hour');" +
                "INSERT INTO public.legacy_instance_daily_entries (realm_id, realm_day, instance_kind, character_id, reservation_id, claimed_at) " +
                $"VALUES (1, DATE '2000-01-02', 3, {fixture[3].Character}, '{other}', now());");
            await ledger.RefundInterruptedAsync(1, DateTimeOffset.UtcNow, refundEntries: true);
            Check.Equal("refunded|1", await ScalarAsync(dataSource,
                $"SELECT close_reason || '|' || refunded_entries FROM public.harbor_attack_runs WHERE reservation_id = '{other}';"),
                "an unpaid interrupted run is still refunded");
        }
        finally
        {
            foreach (var f in fixture)
            {
                await ExecuteAsync(dataSource,
                    $"UPDATE public.character_base SET fighter_job_lv = {f.Level}, fighter_job_exp = {f.Experience} WHERE id = {f.Character};");
            }
            await ExecuteAsync(dataSource,
                $"DELETE FROM public.legacy_instance_daily_entries WHERE realm_day IN (DATE '2000-01-01', DATE '2000-01-02') AND instance_kind = 3;" +
                $"DELETE FROM public.harbor_attack_completion_reward_members WHERE run_reservation_id = '{run}';" +
                $"DELETE FROM public.harbor_attack_completion_rewards WHERE run_reservation_id = '{run}';" +
                $"DELETE FROM public.harbor_attack_runs WHERE reservation_id IN ('{run}', '{other}');");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async Task<(int Level, long Experience, long Revision)> ReadAsync(NpgsqlDataSource dataSource, int id)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT fighter_job_lv, fighter_job_exp, progression_reward_revision FROM public.character_base WHERE id = {id};");
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}
