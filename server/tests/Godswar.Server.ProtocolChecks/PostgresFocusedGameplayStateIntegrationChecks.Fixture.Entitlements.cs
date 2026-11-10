using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFocusedGameplayStateIntegrationChecks
{
    private static async Task InsertBattlePassEntitlementsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        string token,
        DateTimeOffset readAtUtc,
        DateTimeOffset activeExpiresAtUtc)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.account_entitlements (
                account_id,
                entitlement_key,
                scope_key,
                starts_at,
                expires_at,
                revoked_at,
                source,
                source_reference
            )
            VALUES
                (@accountId, 'battle_pass', 'global',
                 @past, @activeExpiry, NULL,
                 'b20c-test', @activeReference),
                (@accountId, 'battle_pass', 'overlap',
                 @older, @overlapExpiry, NULL,
                 'b20c-test', @overlapReference),
                (@accountId, 'battle_pass', 'global',
                 @future, @futureExpiry, NULL,
                 'b20c-test', @futureReference),
                (@accountId, 'battle_pass', 'global',
                 @older, @past, NULL,
                 'b20c-test', @expiredReference),
                (@accountId, 'battle_pass', 'global',
                 @older, @activeExpiry, @past,
                 'b20c-test', @revokedReference);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        AddTimestamp(command, "older", readAtUtc.AddDays(-2));
        AddTimestamp(command, "past", readAtUtc.AddHours(-1));
        AddTimestamp(command, "future", readAtUtc.AddHours(1));
        AddTimestamp(command, "activeExpiry", activeExpiresAtUtc);
        AddTimestamp(command, "overlapExpiry", readAtUtc.AddMinutes(30));
        AddTimestamp(command, "futureExpiry", readAtUtc.AddHours(2));
        command.Parameters.AddWithValue(
            "activeReference",
            $"{token}:battle-pass:active");
        command.Parameters.AddWithValue(
            "overlapReference",
            $"{token}:battle-pass:overlap");
        command.Parameters.AddWithValue(
            "futureReference",
            $"{token}:battle-pass:future");
        command.Parameters.AddWithValue(
            "expiredReference",
            $"{token}:battle-pass:expired");
        command.Parameters.AddWithValue(
            "revokedReference",
            $"{token}:battle-pass:revoked");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertPermanentBattlePassEntitlementsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        string token,
        DateTimeOffset readAtUtc)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.account_entitlements (
                account_id,
                entitlement_key,
                scope_key,
                starts_at,
                expires_at,
                revoked_at,
                source,
                source_reference
            )
            VALUES
                (@accountId, 'battle_pass', 'permanent',
                 @startsAt, NULL, NULL,
                 'b20c-test', @permanentReference),
                (@accountId, 'battle_pass', 'finite-overlap',
                 @startsAt, @finiteExpiry, NULL,
                 'b20c-test', @finiteReference);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        AddTimestamp(command, "startsAt", readAtUtc.AddHours(-1));
        AddTimestamp(command, "finiteExpiry", readAtUtc.AddHours(4));
        command.Parameters.AddWithValue(
            "permanentReference",
            $"{token}:battle-pass:permanent");
        command.Parameters.AddWithValue(
            "finiteReference",
            $"{token}:battle-pass:finite-overlap");
        await command.ExecuteNonQueryAsync();
    }

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value.UtcDateTime;
}
