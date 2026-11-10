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
    public const string BossLootCheckName = "PostgreSQL Wonderland boss corpses grant original sacks once with atomic inventory evidence";

    public static async Task RunBossLootClaimsAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("GODSWAR_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new CheckSkippedException(BossLootCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, @"^godswar_b(?:03|08|09|10|11|12)_[a-z0-9_]{1,48}$",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                throw new CheckSkippedException(BossLootCheckName + " requires a disposable database");
        }
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var game = new PostgresGameStore(connectionString);
        await game.EnsureSeedDataAsync();
        await CheckBossLootMappingAndReplayAsync(source);
        await CheckBossLootRefusalsAsync(source);
        await CheckBossLootCapacityAndAttributesAsync(source);
        await CheckBossLootRollbackAsync(source);
    }

    private static async Task CheckBossLootMappingAndReplayAsync(NpgsqlDataSource source)
    {
        var run = await CreateAsync(source, count: 2);
        var store = new PostgresWonderlandChestClaimStore(source);
        (uint Boss, uint Sack)[] expected = [(46100,4450),(46200,4451),(46201,4451),(46300,4452),
            (46400,4453),(46500,4455),(46600,4456),(46700,4457),(46800,4458),(46801,4459),(46802,4460),(46803,4461)];
        var first = BossRequest(run, expected[0].Boss, expected[0].Sack);
        var results = await Task.WhenAll(store.ClaimBossAsync(first),
            new PostgresWonderlandChestClaimStore(source).ClaimBossAsync(first));
        Check.True(results.Count(result => result.Status == WonderlandChestClaimStatus.Claimed) == 1 &&
            results.Count(result => result.Status == WonderlandChestClaimStatus.AlreadyClaimed) == 1 &&
            results.All(result => result.Rewards.SequenceEqual(new[] { new WonderlandChestItemReward(4450,1,1) })),
            "concurrent corpse clicks freeze and grant one bound original sack without requiring an island title");
        var afterFirst = await BossLootStateAsync(source, first.Subject.CharacterId);
        var replay = await new PostgresWonderlandChestClaimStore(source).ClaimBossAsync(first);
        Check.True(replay.Status == WonderlandChestClaimStatus.AlreadyClaimed && replay.InventoryRevision == 1 &&
            await BossLootStateAsync(source, first.Subject.CharacterId) == afterFirst,
            "restarting the claim store replays the original receipt without duplicating inventory or evidence");
        foreach (var (boss, sack) in expected.Skip(1))
        {
            var request = BossRequest(run, boss, sack);
            var result = await store.ClaimBossAsync(request);
            Check.True(result.Status == WonderlandChestClaimStatus.Claimed &&
                result.Rewards.SequenceEqual(new[] { new WonderlandChestItemReward(sack,1,1) }),
                $"boss {boss} grants its original sack {sack} independently of neighboring bosses");
        }
        await AssertBossLootAsync(source, first.Subject.CharacterId,
            "4450:1:1,4451:1:2,4452:1:1,4453:1:1,4455:1:1,4456:1:1,4457:1:1,4458:1:1,4459:1:1,4460:1:1,4461:1:1", 12, 12);
        var companion = first with { Subject = new(run.FrozenMembers[1].AccountId, run.FrozenMembers[1].CharacterId),
            Ownership = run.FrozenMembers[1].Ownership };
        Check.True((await store.ClaimBossAsync(companion)).Status == WonderlandChestClaimStatus.Claimed,
            "another admitted party member receives an independent sack from the same boss death");
        await AssertBossLootAsync(source, companion.Subject.CharacterId, "4450:1:1", 1, 1);
        var athens = await CreateAsync(source, count: 1);
        var opponent = BossRequest(athens, 46501, 4454, camp: 1);
        Check.True((await store.ClaimBossAsync(opponent)).Status == WonderlandChestClaimStatus.Claimed,
            "an Athens party receives the original Spartan marshal sack");
        await AssertBossLootAsync(source, opponent.Subject.CharacterId, "4454:1:1", 1, 1);
        var nextAdmission = Copy(run, instance: WorldInstanceId.New(), reservation: Guid.NewGuid());
        await InsertAdmissionsAsync(source, nextAdmission);
        var nextRun = BossRequest(nextAdmission, 46100, 4450);
        Check.True((await store.ClaimBossAsync(nextRun)).Status == WonderlandChestClaimStatus.Claimed,
            "a separately admitted run creates a new boss entitlement");
    }

    private static async Task CheckBossLootRefusalsAsync(NpgsqlDataSource source)
    {
        var run = await CreateAsync(source, count: 1);
        var request = BossRequest(run, 46100, 4450);
        var store = new PostgresWonderlandChestClaimStore(source);
        var before = await BossLootStateAsync(source, request.Subject.CharacterId);
        foreach (var invalid in new[] {
            request with { BossObjectId = 46101 }, request with { SackItemId = 4461 },
            request with { SpawnGeneration = 2 }, request with { Island = 8 },
            request with { ReservationId = Guid.Empty }, request with { DeathEventId = Guid.Empty },
            request with { DiedAt = request.StartedAt.AddTicks(-1) },
            request with { DiedAt = request.StartedAt + WonderlandEncounterPolicy.TimeLimit },
            request with { RealmId = new RealmId(2) } })
            Check.True((await store.ClaimBossAsync(invalid)).Status == WonderlandChestClaimStatus.NotEligible,
                "invalid boss identity, sack, generation, death proof, island, and realm cannot grant loot");
        Check.True((await store.ClaimBossAsync(request with {
            Ownership = new PlayerOwnershipFence(Guid.NewGuid(), request.Ownership.Generation) })).Status ==
            WonderlandChestClaimStatus.OwnershipLost, "a stale owner cannot grant or replay a boss sack");
        Check.Equal(before, await BossLootStateAsync(source, request.Subject.CharacterId),
            "invalid requests and ownership failures preserve all inventory, wallet, and durable evidence");
        Check.True((await store.ClaimBossAsync(request)).Status == WonderlandChestClaimStatus.Claimed,
            "the valid corpse claim remains usable after rejected attempts");
        var committed = await BossLootStateAsync(source, request.Subject.CharacterId);
        foreach (var altered in new[] { request with { DeathEventId = Guid.NewGuid() },
            request with { DiedAt = request.DiedAt.AddTicks(1) }, request with { ReservationId = Guid.NewGuid() } })
            Check.True((await store.ClaimBossAsync(altered)).Status == WonderlandChestClaimStatus.NotEligible,
                "an existing boss entitlement cannot be replayed with altered frozen death evidence");
        Check.Equal(committed, await BossLootStateAsync(source, request.Subject.CharacterId),
            "conflicting retries cannot change the first committed reward or its evidence");
        var alternateRun = await CreateAsync(source, count: 1);
        var alternate = alternateRun.FrozenMembers[0];
        await using (var move = source.CreateCommand("""
            UPDATE character_base SET lifecycle_state='deleted',checkpoint_owner_id=NULL,
                deleted_at=GREATEST(clock_timestamp(),"Register_time"),
                restore_until=GREATEST(clock_timestamp(),"Register_time")+interval '1 day',
                purge_after=GREATEST(clock_timestamp(),"Register_time")+interval '2 days'
                WHERE id=@previous;
            UPDATE character_base SET account_id=@account WHERE id=@character;
            """))
        {
            move.Parameters.AddWithValue("account", request.Subject.AccountId);
            move.Parameters.AddWithValue("character", alternate.CharacterId);
            move.Parameters.AddWithValue("previous", request.Subject.CharacterId);
            Check.Equal(2, await move.ExecuteNonQueryAsync(), "prepare a recreated character in the claimed account's existing slot");
        }
        var alternateFence = await PlayerOwnershipTestFences.InstallAsync(source, request.Subject.AccountId, alternate.CharacterId);
        var alternateRequest = request with { Subject = new(request.Subject.AccountId, alternate.CharacterId),
            Ownership = alternateFence };
        var alternateBefore = await BossLootStateAsync(source, alternate.CharacterId);
        Check.True((await store.ClaimBossAsync(alternateRequest)).Status == WonderlandChestClaimStatus.NotEligible &&
            await BossLootStateAsync(source, alternate.CharacterId) == alternateBefore,
            "recreating a character on the same account cannot duplicate a boss entitlement");
    }

    private static WonderlandBossLootClaimRequest BossRequest(WonderlandTitleRequest run, uint boss, uint sack, byte camp = 0)
    {
        var member = run.FrozenMembers[0];
        return new(run.WorldInstanceId, run.RealmId, run.AdmissionReservationId, run.StartedAtUtc,
            run.StartedAtUtc.AddMinutes((boss - 46000) / 100), boss, 1, Guid.NewGuid(),
            checked((int)((boss - 46000) / 100)), camp, sack, new(member.AccountId, member.CharacterId), member.Ownership);
    }
}
