using System.Text.RegularExpressions;
using Godswar.Server.Application.Progression;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGameplayFeatureAdapterChecks
{
    public const string CheckName = "PostgreSQL extracted reward and weekend adapters";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("GODSWAR_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException("GODSWAR_TEST_POSTGRES_CONNECTION_STRING is not set");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var command = dataSource.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, @"^godswar_(?:b09|b12)_[a-z0-9_]{1,40}$"))
                throw new CheckSkippedException("A disposable B09/B12 database is required");
        }
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();
        var fixture = await CreateFixtureAsync(dataSource);
        await CheckLootAsync(dataSource, store, fixture);
        await CheckPetExperienceAsync(dataSource, store, fixture);
        await CheckWeekendAsync(dataSource, fixture);
    }

    private static async Task CheckLootAsync(NpgsqlDataSource dataSource,
        PostgresGameStore store, FeatureFixture fixture)
    {
        var death = Guid.NewGuid();
        var added = await store.PickupMonsterLootAsync(fixture.AccountId, fixture.CharacterId,
            death, 0, 3100, 2);
        Check.True(added.Status == MonsterLootPickupStatus.Added &&
            added.Character?.Id == fixture.CharacterId, "loot adapter commits and reloads its character projection");
        var replay = await store.PickupMonsterLootAsync(fixture.AccountId, fixture.CharacterId,
            death, 0, 3100, 2);
        Check.True(replay.Status == MonsterLootPickupStatus.Duplicate,
            "the same death/loot slot replays without adding items");
        var conflict = await store.PickupMonsterLootAsync(fixture.AccountId, fixture.CharacterId,
            death, 0, 3100, 3);
        Check.True(conflict.Status == MonsterLootPickupStatus.RequestConflict,
            "a reused loot identity cannot alter quantity");
        await using var command = dataSource.CreateCommand("""
            SELECT inventory_revision,
                (SELECT sum(stack)::bigint FROM character_items WHERE user_id = @characterId AND prop_id = 3100),
                (SELECT count(*) FROM monster_loot_pickup_claims WHERE character_id = @characterId),
                (SELECT count(*) FROM character_inventory_ledger WHERE character_id = @characterId)
            FROM character_base WHERE id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync() && reader.GetInt64(0) == 1 &&
            reader.GetInt64(1) == 2 && reader.GetInt64(2) == 1 && reader.GetInt64(3) > 0,
            "loot replay/conflict preserve one inventory revision, quantity, claim, and ledger evidence");
    }

    private static async Task CheckPetExperienceAsync(NpgsqlDataSource dataSource,
        PostgresGameStore store, FeatureFixture fixture)
    {
        var absentDeath = Guid.NewGuid();
        var absent = await store.ApplyPetMonsterKillExperienceAsync(
            fixture.AccountId, fixture.CharacterId, absentDeath, 9);
        Check.True(absent.Status == PetMonsterExperienceStatus.NoSummonedPet,
            "a kill without a summoned pet records a zero-award claim");
        var petId = await InsertPetAsync(dataSource, fixture.CharacterId);
        var absentReplay = await store.ApplyPetMonsterKillExperienceAsync(
            fixture.AccountId, fixture.CharacterId, absentDeath, 9);
        Check.True(absentReplay.Status == PetMonsterExperienceStatus.Duplicate &&
            !absentReplay.HasPetProjection, "summoning later cannot turn a replay into a new award");
        var death = Guid.NewGuid();
        var awarded = await store.ApplyPetMonsterKillExperienceAsync(
            fixture.AccountId, fixture.CharacterId, death, 9);
        Check.True(awarded.Status == PetMonsterExperienceStatus.Applied &&
            awarded.PetId == petId && awarded.TotalExperience == 109 && awarded.PetRevision == 1,
            "the extracted pet adapter awards and advances exactly one pet revision");
        var replay = await store.ApplyPetMonsterKillExperienceAsync(
            fixture.AccountId, fixture.CharacterId, death, 9);
        var conflict = await store.ApplyPetMonsterKillExperienceAsync(
            fixture.AccountId, fixture.CharacterId, death, 10);
        Check.True(replay.Status == PetMonsterExperienceStatus.Duplicate &&
            replay.TotalExperience == 109 && conflict.Status == PetMonsterExperienceStatus.RequestConflict,
            "pet award replay preserves its result and conflicting amounts are rejected");
        await using var command = dataSource.CreateCommand("""
            SELECT experience, revision,
                (SELECT count(*) FROM monster_death_pet_experience WHERE character_id = @characterId)
            FROM character_pets WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync() && reader.GetInt64(0) == 109 &&
            reader.GetInt64(1) == 1 && reader.GetInt64(2) == 2,
            "two death identities retain two claims while only one changes the pet");
    }

    private static async Task CheckWeekendAsync(NpgsqlDataSource dataSource, FeatureFixture fixture)
    {
        IWeekendExperienceClaimStore claims = new PostgresWeekendExperienceClaimStore(dataSource);
        var saturday = new DateOnly(2026, 9, 5);
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var realm = new RealmId(fixture.RealmId);
        Check.True(await claims.ClaimWeekendExperienceAsync(fixture.AccountId, fixture.CharacterId,
            realm, saturday, now) == WeekendExperienceClaimStatus.Granted, "first weekend claim grants the boost");
        await using (var consume = dataSource.CreateCommand("""
            UPDATE character_experience_modifiers SET remaining_online_ticks = @ticks
            WHERE character_id = @characterId AND kind = 22;
            """))
        {
            consume.Parameters.AddWithValue("ticks", TimeSpan.FromHours(4).Ticks);
            consume.Parameters.AddWithValue("characterId", fixture.CharacterId);
            Check.Equal(1, await consume.ExecuteNonQueryAsync(), "fixture consumes four online boost hours");
        }
        Check.True(await claims.ClaimWeekendExperienceAsync(fixture.AccountId, fixture.CharacterId,
            realm, saturday, now.AddHours(1)) == WeekendExperienceClaimStatus.AlreadyClaimed,
            "same-day weekend claim cannot refill consumed time");
        Check.Equal(TimeSpan.FromHours(4).Ticks, await ReadWeekendTicksAsync(dataSource, fixture.CharacterId),
            "same-day replay preserves the remaining online duration");
        Check.True(await claims.ClaimWeekendExperienceAsync(fixture.AccountId + 100_000, fixture.CharacterId,
            realm, saturday.AddDays(7), now.AddDays(7)) == WeekendExperienceClaimStatus.CharacterNotFound,
            "a different account cannot refresh the character's boost");
        Check.True(await claims.ClaimWeekendExperienceAsync(fixture.AccountId, fixture.CharacterId,
            realm, saturday.AddDays(7), now.AddDays(7)) == WeekendExperienceClaimStatus.Granted,
            "a later weekend claim replaces the prior boost");
        Check.Equal(WeekendExperienceClaimRules.Duration.Ticks,
            await ReadWeekendTicksAsync(dataSource, fixture.CharacterId), "later claim receives exactly eight online hours");
    }

    private static async Task<long> ReadWeekendTicksAsync(NpgsqlDataSource dataSource, int characterId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT remaining_online_ticks FROM character_experience_modifiers
            WHERE character_id = @characterId AND kind = 22;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
