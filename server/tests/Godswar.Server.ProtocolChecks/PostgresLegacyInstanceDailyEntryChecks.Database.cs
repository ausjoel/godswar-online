using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresLegacyInstanceDailyEntryChecks
{
    private static async Task InsertVersion132ClaimAsync(
        NpgsqlDataSource dataSource,
        RealmId realmId,
        DateOnly day,
        IReadOnlyCollection<int> characterIds)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.legacy_instance_daily_entries (
                realm_id,
                realm_day,
                instance_kind,
                character_id,
                reservation_id,
                claimed_at)
            SELECT
                @realmId,
                @realmDay,
                1,
                character_id,
                @reservationId,
                @claimedAt
            FROM unnest(@characterIds) AS character_id;
            """);
        command.Parameters.AddWithValue("realmId", (short)realmId.Value);
        command.Parameters.AddWithValue("realmDay", day);
        command.Parameters.AddWithValue(
            "characterIds",
            characterIds.ToArray());
        command.Parameters.AddWithValue("reservationId", Guid.NewGuid());
        command.Parameters.AddWithValue(
            "claimedAt",
            new DateTime(2026, 8, 31, 1, 0, 0, DateTimeKind.Utc));
        Check.Equal(
            characterIds.Count,
            await command.ExecuteNonQueryAsync(),
            "version-132 fixture records one consumed party entry");
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        NpgsqlDataSource dataSource,
        string migrationId)
    {
        await using (var existenceCommand = dataSource.CreateCommand(
                         "SELECT to_regclass(" +
                         "'public.schema_migrations') IS NOT NULL;"))
        {
            if (!(bool)(await existenceCommand.ExecuteScalarAsync())!)
            {
                return false;
            }
        }

        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (" +
            "SELECT 1 FROM public.schema_migrations " +
            "WHERE migration_id = @migrationId);");
        command.Parameters.AddWithValue("migrationId", migrationId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static IReadOnlyList<PostgresSchemaMigration> MigrationsThrough(
        string migrationId)
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var finalIndex = migrations
            .Select(static (migration, index) => (migration, index))
            .Single(candidate => candidate.migration.Id == migrationId)
            .index;
        return migrations.Take(finalIndex + 1).ToArray();
    }

    private static async Task<short> ReadFreeLimitAsync(
        NpgsqlDataSource dataSource,
        InstanceCallerEntryKind kind) =>
        await ReadPolicyValueAsync(
            dataSource,
            kind,
            "free_entry_limit");

    private static async Task<short?> ReadPaidRetryLimitAsync(
        NpgsqlDataSource dataSource,
        InstanceCallerEntryKind kind)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT paid_retry_limit FROM legacy_instance_settings " +
            "WHERE instance_kind = @instanceKind;");
        command.Parameters.AddWithValue("instanceKind", (short)kind);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : (short)value!;
    }

    private static async Task<short> ReadPolicyValueAsync(
        NpgsqlDataSource dataSource,
        InstanceCallerEntryKind kind,
        string column)
    {
        var sql = column switch
        {
            "free_entry_limit" =>
                "SELECT free_entry_limit FROM legacy_instance_settings ",
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };
        await using var command = dataSource.CreateCommand(
            sql + "WHERE instance_kind = @instanceKind;");
        command.Parameters.AddWithValue("instanceKind", (short)kind);
        return (short)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetPolicyAsync(
        NpgsqlDataSource dataSource,
        InstanceCallerEntryKind kind,
        short freeLimit,
        short? paidRetryLimit)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE legacy_instance_settings " +
            "SET free_entry_limit = @freeLimit, " +
            "paid_retry_limit = @paidRetryLimit, " +
            "updated_at = clock_timestamp() " +
            "WHERE instance_kind = @instanceKind;");
        command.Parameters.AddWithValue("instanceKind", (short)kind);
        command.Parameters.AddWithValue("freeLimit", freeLimit);
        command.Parameters.AddWithValue(
            "paidRetryLimit",
            paidRetryLimit is null ? DBNull.Value : paidRetryLimit.Value);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"{kind} setting row remains present");
    }

    private static async Task SetVersion133PolicyAsync(
        NpgsqlDataSource dataSource,
        InstanceCallerEntryKind kind,
        short dailyLimit,
        short freeLimit)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE legacy_instance_settings " +
            "SET daily_entry_limit = @dailyLimit, " +
            "free_entry_limit = @freeLimit, " +
            "updated_at = clock_timestamp() " +
            "WHERE instance_kind = @instanceKind;");
        command.Parameters.AddWithValue("instanceKind", (short)kind);
        command.Parameters.AddWithValue("dailyLimit", dailyLimit);
        command.Parameters.AddWithValue("freeLimit", freeLimit);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"version-133 {kind} setting row remains present");
    }

    private static async Task CheckWonderlandPaidPolicyRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        foreach (short? invalidLimit in new short?[] { 1, null })
        {
            var rejected = false;
            try
            {
                await using var command = dataSource.CreateCommand(
                    "UPDATE legacy_instance_settings " +
                    "SET paid_retry_limit = @paidRetryLimit " +
                    "WHERE instance_kind = 2;");
                command.Parameters.AddWithValue(
                    "paidRetryLimit",
                    invalidLimit is null
                        ? DBNull.Value
                        : invalidLimit.Value);
                _ = await command.ExecuteNonQueryAsync();
            }
            catch (PostgresException error)
                when (error.SqlState == PostgresErrorCodes.CheckViolation)
            {
                rejected = true;
            }
            Check.True(
                rejected,
                "the database rejects unreachable paid Wonderland " +
                $"policy '{invalidLimit?.ToString() ?? "unlimited"}'");
        }
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteAccountsAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<int> accountIds)
    {
        if (accountIds.Count == 0)
        {
            return;
        }
        await using var command = dataSource.CreateCommand(
            "DELETE FROM public.accounts WHERE id = ANY(@accountIds);");
        command.Parameters.AddWithValue("accountIds", accountIds.ToArray());
        _ = await command.ExecuteNonQueryAsync();
    }
}
