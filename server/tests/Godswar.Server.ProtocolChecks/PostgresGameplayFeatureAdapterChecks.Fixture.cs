using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGameplayFeatureAdapterChecks
{
    private sealed record FeatureFixture(int AccountId, int CharacterId, int RealmId);

    private static async Task<FeatureFixture> CreateFixtureAsync(NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var account = new NpgsqlCommand("""
            INSERT INTO accounts (username, password) VALUES (@name, '') RETURNING id;
            """, connection, transaction);
        account.Parameters.AddWithValue("name", "feature_" + token);
        var accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
        await using var realm = new NpgsqlCommand("SELECT id FROM server ORDER BY id LIMIT 1;", connection, transaction);
        var realmId = Convert.ToInt32(await realm.ExecuteScalarAsync());
        await using var character = new NpgsqlCommand("""
            INSERT INTO character_base (account_id, server_id, name, camp, profession,
                fighter_job_lv, "Money", "Stone", "BindingGold", wallet_revision, inventory_revision)
            VALUES (@accountId, @realmId, @name, 1, 0, 80, 1000, 0, 0, 0, 0) RETURNING id;
            """, connection, transaction);
        character.Parameters.AddWithValue("accountId", accountId);
        character.Parameters.AddWithValue("realmId", realmId);
        character.Parameters.AddWithValue("name", "Feature" + token);
        var characterId = Convert.ToInt32(await character.ExecuteScalarAsync());
        Check.True(await PostgresCharacterEconomyBaseline.EnsureAsync(connection, transaction,
            accountId, characterId, 30, CancellationToken.None), "feature fixture has an economy baseline");
        await transaction.CommitAsync();
        return new(accountId, characterId, realmId);
    }

    private static async Task<long> InsertPetAsync(NpgsqlDataSource dataSource, int characterId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO character_pets (user_id, species_id, name, sex, level, experience,
                aptitude, current_energy, maximum_energy, amity, satiety, remaining_lifetime,
                activity_state, revision, is_carried, is_summoned,
                initial_savvy_baseline_total, initial_savvy_policy_version,
                rarity_added_savvy_baseline_total, rarity_added_savvy_policy_version,
                initial_savvy_source_version, birth_rank, hatch_rank_roll,
                hatch_rank_outcome_order, hatch_rank_content_revision)
            VALUES (@characterId, 1, 'Reward Fixture', 0, 1, 100,
                1, 100, 100, 100, 100, 600, 'owned', 0, true, true,
                303, @policy, 303, @policy, @source,
                (SELECT step.rank FROM pet_content_publication publication
                 JOIN pet_content_hatch_rank_steps step ON step.revision = publication.revision
                 AND step.aptitude = 1 AND step.outcome_order = 0 WHERE publication.family = 'pets'),
                0, 0, (SELECT revision FROM pet_content_publication WHERE family = 'pets'))
            RETURNING id;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("policy", PetInitialSavvyPolicy.Version);
        command.Parameters.AddWithValue("source", PetSavvyRuntimeSemantics.SourceVersion);
        var petId = Convert.ToInt64(await command.ExecuteScalarAsync());
        await using var stats = new NpgsqlCommand("""
            INSERT INTO character_pet_stat_values (pet_id, stat_code,
                initial_savvy, added_savvy, base_growth_rate, birth_initial_savvy,
                rarity_added_savvy, growth_acceleration, revision)
            SELECT @petId, code, 50.5, 0.01, 0.01, 50.5, 50.5, 0, 0
            FROM generate_series(1, 6) AS code;
            """, connection, transaction);
        stats.Parameters.AddWithValue("petId", petId);
        Check.Equal(6, await stats.ExecuteNonQueryAsync(),
            "reward fixture creates all six stat rows with the declared birth-savvy total");
        await transaction.CommitAsync();
        return petId;
    }
}
