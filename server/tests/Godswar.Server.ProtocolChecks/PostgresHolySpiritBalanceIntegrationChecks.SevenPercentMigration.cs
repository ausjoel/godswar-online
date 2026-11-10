using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolySpiritBalanceIntegrationChecks
{
    private const string SevenPercentMigrationId =
        "20260821_104_cooled_holy_stone_reduction_7_percent";

    private static async Task AssertSevenPercentForwardMigrationAsync(
        NpgsqlDataSource dataSource)
    {
        var migration = PostgresSchemaMigrationCatalog
            .CreateCooledHolyStoneSevenPercentReduction();
        var prefix = PostgresSchemaMigrationCatalog.All
            .TakeWhile(entry => entry.Id != SevenPercentMigrationId)
            .ToArray();
        Check.True(
            prefix.Length + 1 <= PostgresSchemaMigrationCatalog.All.Count &&
            PostgresSchemaMigrationCatalog.All[prefix.Length].Id ==
                SevenPercentMigrationId,
            "migration 104 is registered after its complete prerequisite " +
            "prefix");

        var runner = new PostgresSchemaMigrationRunner(dataSource);
        await runner.InitializeAsync(LegacySchemaBootstrap.LoadAsync, prefix);
        var fixture = await SeedSevenPercentMigrationFixtureAsync(dataSource);
        try
        {
            var before = await ReadMigrationValuesAsync(dataSource, fixture);
            await AssertSettingsGuardRollbackAsync(
                dataSource,
                migration,
                fixture,
                before);
            await AssertExplicitTransactionRollbackAsync(
                dataSource,
                migration,
                fixture,
                before);

            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                PostgresSchemaMigrationCatalog.All);
            var after = await ReadMigrationValuesAsync(dataSource, fixture);
            AssertExactPromotion(before, after);
            var balance = await PostgresHolySpiritBalanceSnapshotReader
                .LoadAsync(dataSource);
            Check.True(
                balance.CooledPhysicalReductionGradeOneMaximum == 70 &&
                balance.CooledMagicReductionGradeOneMaximum == 70 &&
                balance.CooledCriticalReductionGradeOneMaximum == 60 &&
                balance.UpdatedBy == "migration-104",
                "migration 104 commits the 7.0/7.0/6.0 managed balance");
        }
        finally
        {
            await CleanupFixtureAsync(dataSource, fixture.Core);
        }
    }

    private static async Task AssertSettingsGuardRollbackAsync(
        NpgsqlDataSource dataSource,
        PostgresSchemaMigration migration,
        SevenPercentMigrationFixture fixture,
        short?[] before)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var drift = new NpgsqlCommand(
                         """
                         UPDATE public.holy_spirit_balance_settings
                         SET cooled_physical_reduction_grade_one_maximum = 56,
                             updated_by = 'migration-104-guard-check'
                         WHERE setting_id = 1;
                         """,
                         connection,
                         transaction))
        {
            await drift.ExecuteNonQueryAsync();
        }

        var rejected = false;
        try
        {
            await using var command = new NpgsqlCommand(
                migration.Sql,
                connection,
                transaction);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException error) when (
            error.SqlState == PostgresErrorCodes.CheckViolation)
        {
            rejected = true;
        }
        await transaction.RollbackAsync();

        Check.True(rejected, "migration 104 rejects unexpected live settings");
        var afterRejection = await ReadMigrationValuesAsync(
            dataSource,
            fixture);
        Check.True(
            before.SequenceEqual(afterRejection),
            "a rejected migration leaves every socket value unchanged");
        await AssertBalanceAsync(
            dataSource,
            55,
            55,
            60,
            "the rejected settings guard rolls back atomically");
    }

    private static async Task AssertExplicitTransactionRollbackAsync(
        NpgsqlDataSource dataSource,
        PostgresSchemaMigration migration,
        SevenPercentMigrationFixture fixture,
        short?[] before)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
                         migration.Sql,
                         connection,
                         transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        var inside = await ReadMigrationValuesAsync(
            connection,
            transaction,
            fixture);
        AssertExactPromotion(before, inside);
        await AssertBalanceAsync(
            connection,
            transaction,
            70,
            70,
            60,
            "migration values are visible together inside its transaction");
        await transaction.RollbackAsync();

        var afterRollback = await ReadMigrationValuesAsync(
            dataSource,
            fixture);
        Check.True(
            before.SequenceEqual(afterRollback),
            "rolling back migration 104 restores every promoted socket");
        await AssertBalanceAsync(
            dataSource,
            55,
            55,
            60,
            "rolling back migration 104 restores the managed balance");
    }

    private static void AssertExactPromotion(
        short?[] before,
        short?[] after)
    {
        short?[] expected =
        [
            700, 700, 549, 551,
            549, 551, 700, 700,
            600, 599, 601, null
        ];
        Check.True(
            after.SequenceEqual(expected),
            "migration 104 promotes only exact old physical/magic maxima");
        Check.Equal(
            4,
            before.Zip(after).Count(pair => pair.First != pair.Second),
            "migration 104 changes the exact four reviewed fixture sockets");
    }

    private static async Task<SevenPercentMigrationFixture>
        SeedSevenPercentMigrationFixtureAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var core = await SeedMigrationOwnerAsync(connection, transaction);
        var first = await InsertMigrationItemAsync(
            connection,
            transaction,
            core.CharacterId,
            core.TemplateId,
            0,
            [9, 10, 9, 10],
            [550, 550, 549, 551]);
        var second = await InsertMigrationItemAsync(
            connection,
            transaction,
            core.CharacterId,
            core.TemplateId,
            1,
            [9, 10, 9, 10],
            [549, 551, 550, 550]);
        var critical = await InsertMigrationItemAsync(
            connection,
            transaction,
            core.CharacterId,
            core.TemplateId,
            2,
            [13, 13, 13, 13],
            [600, 599, 601, null]);
        await transaction.CommitAsync();
        return new(core, first, second, critical);
    }

    private static async Task<MigrationOwnerFixture> SeedMigrationOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        int accountId;
        await using (var account = new NpgsqlCommand(
                         """
                         INSERT INTO public.accounts (username, password)
                         VALUES (@username, '')
                         RETURNING id;
                         """,
                         connection,
                         transaction))
        {
            account.Parameters.AddWithValue(
                "username",
                $"holy_7pct_{Guid.NewGuid():N}"[..32]);
            accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
                         """
                         INSERT INTO public.character_base (
                             account_id, server_id, name, camp, profession,
                             fighter_job_lv, "Money", "Stone")
                         VALUES (@accountId, 1, @name, 1, 0, 80, 0, 0)
                         RETURNING id;
                         """,
                         connection,
                         transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue(
                "name",
                $"HolySeven{Guid.NewGuid():N}"[..32]);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync());
        }

        await using var template = new NpgsqlCommand(
            "SELECT id FROM public.item_templates ORDER BY id LIMIT 1;",
            connection,
            transaction);
        var templateId = Convert.ToInt32(await template.ExecuteScalarAsync());
        return new(accountId, characterId, templateId);
    }

    private static async Task<long> InsertMigrationItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int templateId,
        short slot,
        short[] effects,
        short?[] values)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level,
                holy_socket4_effect_id, holy_socket4_level,
                holy_socket1_value, holy_socket2_value,
                holy_socket3_value, holy_socket4_value)
            VALUES (
                @characterId, 0, @slot, @templateId, 4,
                @effect1, 10, @effect2, 10,
                @effect3, 10, @effect4, 10,
                @value1, @value2, @value3, @value4)
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue("templateId", templateId);
        for (var index = 0; index < 4; index++)
        {
            command.Parameters.AddWithValue($"effect{index + 1}", effects[index]);
            command.Parameters.AddWithValue(
                $"value{index + 1}",
                values[index] is { } value ? value : DBNull.Value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<short?[]> ReadMigrationValuesAsync(
        NpgsqlDataSource dataSource,
        SevenPercentMigrationFixture fixture)
    {
        var values = new List<short?>(12);
        values.AddRange(await ReadSocketValuesAsync(dataSource, fixture.First));
        values.AddRange(await ReadSocketValuesAsync(dataSource, fixture.Second));
        values.AddRange(await ReadSocketValuesAsync(dataSource, fixture.Critical));
        return values.ToArray();
    }

    private static async Task<short?[]> ReadMigrationValuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SevenPercentMigrationFixture fixture)
    {
        var values = new List<short?>(12);
        foreach (var itemId in new[]
                 {
                     fixture.First,
                     fixture.Second,
                     fixture.Critical
                 })
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT holy_socket1_value, holy_socket2_value,
                       holy_socket3_value, holy_socket4_value
                FROM public.character_items
                WHERE id = @itemId;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("itemId", itemId);
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync(), "migration fixture item exists");
            for (var ordinal = 0; ordinal < 4; ordinal++)
            {
                values.Add(reader.IsDBNull(ordinal)
                    ? null
                    : reader.GetInt16(ordinal));
            }
        }
        return values.ToArray();
    }

    private static async Task AssertBalanceAsync(
        NpgsqlDataSource dataSource,
        int physical,
        int magic,
        int critical,
        string context)
    {
        var balance = await PostgresHolySpiritBalanceSnapshotReader
            .LoadAsync(dataSource);
        Check.True(
            balance.CooledPhysicalReductionGradeOneMaximum == physical &&
            balance.CooledMagicReductionGradeOneMaximum == magic &&
            balance.CooledCriticalReductionGradeOneMaximum == critical,
            context);
    }

    private static async Task AssertBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int physical,
        int magic,
        int critical,
        string context)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT cooled_physical_reduction_grade_one_maximum,
                   cooled_magic_reduction_grade_one_maximum,
                   cooled_critical_reduction_grade_one_maximum
            FROM public.holy_spirit_balance_settings
            WHERE setting_id = 1;
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt16(0) == physical &&
            reader.GetInt16(1) == magic &&
            reader.GetInt16(2) == critical,
            context);
    }

    private sealed record MigrationOwnerFixture(
        int AccountId,
        int CharacterId,
        int TemplateId);

    private sealed record SevenPercentMigrationFixture(
        MigrationOwnerFixture Owner,
        long First,
        long Second,
        long Critical)
    {
        public BalanceSocketFixture Core => new(
            Owner.AccountId,
            Owner.CharacterId,
            First,
            Second,
            Critical,
            First);
    }
}
