using System.Data;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceDailyEntryClaimStore
{
    public async Task<int> RecoverPendingAsync(
        RealmId realmId,
        DateTimeOffset staleBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRecoveryRealm(realmId);
        if (staleBeforeUtc == default ||
            staleBeforeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Daily-entry recovery requires a UTC stale cutoff.",
                nameof(staleBeforeUtc));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            WITH candidates AS (
                SELECT
                    entry.realm_id,
                    entry.realm_day,
                    entry.instance_kind,
                    entry.character_id,
                    entry.reservation_id
                FROM public.legacy_instance_daily_entries entry
                JOIN public.character_base character
                  ON character.id = entry.character_id
                WHERE entry.realm_id = @realmId
                  AND entry.admitted_at IS NULL
                  AND entry.claimed_at < @staleBefore
                  AND character.checkpoint_owner_id IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM public.legacy_instance_opal_payments payment
                      WHERE payment.reservation_id = entry.reservation_id
                        AND payment.character_id = entry.character_id
                        AND payment.payment_status IN (
                            'pending', 'committed'))
                ORDER BY entry.claimed_at, entry.character_id
                FOR UPDATE OF entry, character SKIP LOCKED
            )
            DELETE FROM public.legacy_instance_daily_entries entry
            USING candidates
            WHERE entry.realm_id = candidates.realm_id
              AND entry.realm_day = candidates.realm_day
              AND entry.instance_kind = candidates.instance_kind
              AND entry.character_id = candidates.character_id
              AND entry.reservation_id = candidates.reservation_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)realmId.Value));
        command.Parameters.Add(
            "staleBefore",
            NpgsqlDbType.TimestampTz).Value =
            staleBeforeUtc.UtcDateTime;
        var recovered = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return recovered;
    }

    public async Task<int> RecoverCharacterAsync(
        RealmId realmId,
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        ValidateRecoveryRealm(realmId);
        CharacterCheckpointValidation.ValidateIdentity(
            accountId,
            characterId,
            ownership);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        if (!await LockCurrentOwnershipAsync(
                connection,
                transaction,
                accountId,
                characterId,
                ownership,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "Legacy-instance login recovery lost character ownership.");
        }

        await using var command = new NpgsqlCommand(
            """
            DELETE FROM public.legacy_instance_daily_entries entry
            WHERE entry.realm_id = @realmId
              AND entry.character_id = @characterId
              AND entry.admitted_at IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM public.legacy_instance_opal_payments payment
                  WHERE payment.reservation_id = entry.reservation_id
                    AND payment.character_id = entry.character_id
                    AND payment.payment_status IN (
                        'pending', 'committed'));
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)realmId.Value));
        command.Parameters.AddWithValue("characterId", characterId);
        var recovered = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return recovered;
    }

    private static async Task<bool> LockCurrentOwnershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                checkpoint_owner_id,
                checkpoint_owner_generation
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
               !reader.IsDBNull(0) &&
               reader.GetGuid(0) == ownership.OwnerId &&
               reader.GetInt64(1) == ownership.Generation;
    }

    private static void ValidateRecoveryRealm(RealmId realmId)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
    }
}
