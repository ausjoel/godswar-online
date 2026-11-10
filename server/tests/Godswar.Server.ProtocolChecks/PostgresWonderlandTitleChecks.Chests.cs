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
    public const string ChestCheckName = "PostgreSQL Wonderland chests require earned eligibility and grant four gems exactly once";

    public static async Task RunChestClaimsAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("GODSWAR_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new CheckSkippedException(ChestCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, @"^godswar_b(?:03|08|09|10|11|12)_[a-z0-9_]{1,48}$",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                throw new CheckSkippedException(ChestCheckName + " requires a disposable database");
        }
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var game = new PostgresGameStore(connectionString);
        await game.EnsureSeedDataAsync();
        await CheckChestEligibilityAndReplayAsync(source);
        await CheckChestStackingAsync(source);
        await CheckFinalChestCapacityAsync(source);
        await CheckChestTransactionRollbackAsync(source);
    }

    private static async Task CheckChestEligibilityAndReplayAsync(NpgsqlDataSource source)
    {
        var milestone = await CreateAsync(source, count: 3, eligibleCount: 2);
        var rolls = new FixedWonderlandGemRoll(15);
        var store = new PostgresWonderlandChestClaimStore(source, rolls);
        var request = ChestRequest(milestone);
        Check.True((await store.ClaimAsync(request)).Status == WonderlandChestClaimStatus.NotEligible,
            "admission without a persisted clear cannot grant a chest");
        Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(milestone)).Succeeded,
            "persist frozen island-clear eligibility before claiming");
        var before = await ChestStateAsync(source, request.Subject.CharacterId);
        foreach (var invalid in new[] {
            request with { WorldInstanceId = WorldInstanceId.New() }, request with { Island = 1 },
            request with { MilestoneHash = new string('F',64) }, request with { RealmId = new RealmId(2) },
            ChestRequest(milestone, milestone.AdmittedMembers[2]) })
            Check.True((await store.ClaimAsync(invalid)).Status == WonderlandChestClaimStatus.NotEligible,
                "wrong instance, uncleared island, hash, realm, and absent-at-clear member cannot claim");
        Check.True((await store.ClaimAsync(request with {
            Ownership = new PlayerOwnershipFence(Guid.NewGuid(), request.Ownership.Generation) })).Status ==
            WonderlandChestClaimStatus.OwnershipLost, "a stale session fence cannot claim an earned chest");
        Check.Equal(before, await ChestStateAsync(source, request.Subject.CharacterId),
            "rejected claims change no wallet, inventory, claim, or command evidence");

        var claims = await Task.WhenAll(store.ClaimAsync(request), new PostgresWonderlandChestClaimStore(source, rolls).ClaimAsync(request));
        Check.True(claims.Count(receipt => receipt.Status == WonderlandChestClaimStatus.Claimed) == 1 &&
            claims.Count(receipt => receipt.Status == WonderlandChestClaimStatus.AlreadyClaimed) == 1 &&
            claims.All(receipt => receipt.Rewards.SequenceEqual(new[] { new WonderlandChestItemReward(4213,4,1) })) && rolls.Calls == 1,
            "simultaneous clicks settle four gems with one roll and replay the same entitlement");
        await AssertChestSacksAsync(source, request.Subject.CharacterId, "4213:1:4", revision: 1, claims: 1, ledger: 1);
        var after = await ChestStateAsync(source, request.Subject.CharacterId);
        Check.True((await new PostgresWonderlandChestClaimStore(source).ClaimAsync(request)).Status ==
            WonderlandChestClaimStatus.AlreadyClaimed && await ChestStateAsync(source, request.Subject.CharacterId) == after,
            "a restarted claim store replays without issuing another item or inventory revision");
        var companion = ChestRequest(milestone, milestone.FrozenMembers[1]);
        Check.True((await store.ClaimAsync(companion)).Status == WonderlandChestClaimStatus.Claimed,
            "each eligible original party member has an independent island claim");
        await AssertChestSacksAsync(source, companion.Subject.CharacterId, "4213:1:4", revision: 1, claims: 1, ledger: 1);
    }

    private static async Task CheckChestStackingAsync(NpgsqlDataSource source)
    {
        var milestone = await CreateAsync(source, count: 1);
        var request = ChestRequest(milestone);
        await using (var item = source.CreateCommand("""
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
            VALUES(@character,1,0,4213,1,1,1,98);
            """))
        {
            item.Parameters.AddWithValue("character", request.Subject.CharacterId);
            await item.ExecuteNonQueryAsync();
        }
        var titles = new PostgresWonderlandTitleStore(source);
        Check.True((await titles.SettleAsync(milestone)).Succeeded, "first stacking fixture clear settles");
        var store = new PostgresWonderlandChestClaimStore(source, new FixedWonderlandGemRoll(15));
        Check.True((await store.ClaimAsync(request)).Status == WonderlandChestClaimStatus.Claimed,
            "four bound gems fill a partial stack and roll over without changing its attributes");
        await AssertChestSacksAsync(source, request.Subject.CharacterId, "4213:1:99,4213:1:3", 1, 1, 2);
        var next = Copy(milestone, instance: WorldInstanceId.New(), reservation: Guid.NewGuid(),
            started: milestone.StartedAtUtc.AddDays(1), cleared: milestone.ClearedAtUtc.AddDays(1));
        await InsertAdmissionsAsync(source, next);
        Check.True((await titles.SettleAsync(next)).Succeeded && (await store.ClaimAsync(ChestRequest(next))).Status ==
            WonderlandChestClaimStatus.Claimed, "a new admitted run earns a fresh claim for the same island");
        await AssertChestSacksAsync(source, request.Subject.CharacterId, "4213:1:99,4213:1:7", 2, 2, 3);
    }

    private static async Task CheckFinalChestCapacityAsync(NpgsqlDataSource source)
    {
        var milestone = Copy(await CreateAsync(source, count: 1), island: 8);
        var request = ChestRequest(milestone);
        Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(milestone)).Succeeded,
            "final-island clear is persisted before its four-gem claim");
        await using (var fill = source.CreateCommand("""
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
            SELECT @character,1,slot::smallint,1007,1,1,0,1 FROM generate_series(0,94) slot;
            """))
        {
            fill.Parameters.AddWithValue("character", request.Subject.CharacterId);
            await fill.ExecuteNonQueryAsync();
        }
        var rolls = new FixedWonderlandGemRoll(15);
        var store = new PostgresWonderlandChestClaimStore(source, rolls);
        var before = await ChestStateAsync(source, request.Subject.CharacterId);
        Check.True((await store.ClaimAsync(request)).Status == WonderlandChestClaimStatus.InventoryFull &&
            await ChestStateAsync(source, request.Subject.CharacterId) == before && rolls.Calls == 0,
            "one free slot cannot hold every possible gem mix; refusal rolls nothing and preserves the claim");
        await using (var free = source.CreateCommand("DELETE FROM character_items WHERE user_id=@character AND slot_index=94;"))
        {
            free.Parameters.AddWithValue("character", request.Subject.CharacterId);
            Check.Equal(1, await free.ExecuteNonQueryAsync(), "make exactly one additional test slot available");
        }
        Check.True((await store.ClaimAsync(request)).Status == WonderlandChestClaimStatus.Claimed,
            "inventory-full claim can retry after making room");
        await AssertChestSacksAsync(source, request.Subject.CharacterId, "4213:1:4", 1, 1, 1);
    }

    private static WonderlandChestClaimRequest ChestRequest(WonderlandTitleRequest milestone,
        WonderlandTitleMember? member = null, byte camp = 0)
    {
        var claimant = member ?? milestone.FrozenMembers[0];
        return new(milestone.WorldInstanceId, milestone.RealmId, milestone.IslandNumber, camp, milestone.RequestHash,
            new(claimant.AccountId, claimant.CharacterId), claimant.Ownership);
    }

    private sealed class FixedWonderlandGemRoll(int value) : IWonderlandChestGemRollSource
    {
        public int Calls { get; private set; }
        public int NextRoll() { Calls++; return value; }
    }
}
