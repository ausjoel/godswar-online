using System.Data;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceOpalPaymentStore
{
    public async Task<int> RecoverPendingAsync(
        RealmId realmId,
        DateTimeOffset staleBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        if (!realmId.IsValid ||
            staleBeforeUtc == default ||
            staleBeforeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Opal recovery requires a realm and UTC stale cutoff.");
        }
        var reservationIds = await ReadStaleReservationIdsAsync(
            realmId,
            staleBeforeUtc,
            cancellationToken);
        var recovered = 0;
        foreach (var reservationId in reservationIds)
        {
            try
            {
                await using var connection =
                    await _dataSource.OpenConnectionAsync(
                        cancellationToken);
                await using var transaction =
                    await connection.BeginTransactionAsync(
                        IsolationLevel.ReadCommitted,
                        cancellationToken);
                await LockReservationAsync(
                    connection,
                    transaction,
                    reservationId,
                    cancellationToken);
                var offlineCharacterIds =
                    await LockPendingCharacterIdsIfReservationOfflineAsync(
                        connection,
                        transaction,
                        reservationId,
                        realmId,
                        staleBeforeUtc,
                        cancellationToken);
                if (offlineCharacterIds.Count == 0)
                {
                    await transaction.CommitAsync(cancellationToken);
                    continue;
                }
                var settlement = await SettleLockedAsync(
                    connection,
                    transaction,
                    reservationId,
                    new HashSet<int>(),
                    releaseClaimsWithoutPayments: false,
                    restrictedCharacterIds: offlineCharacterIds,
                    takeoverCharacterId: null,
                    takeoverOwnership: null,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _ = settlement;
                recovered++;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // A temporarily full bag or one corrupt historical row must
                // not prevent other reservations from being reconciled. The
                // failed transaction keeps this payment pending for a later
                // sweep instead of losing its compensating Opal.
                Console.Error.WriteLine(
                    "[instance-opal-recovery] reservation failed " +
                    $"reservation={reservationId}: {error.Message}");
            }
        }

        await ReleaseAlreadyRefundedMemberClaimsAsync(
            realmId,
            cancellationToken);
        return recovered;
    }

    public async Task<int> RecoverCharacterAsync(
        RealmId realmId,
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        CharacterCheckpointValidation.ValidateIdentity(
            accountId,
            characterId,
            ownership);
        var reservationIds = await ReadCharacterReservationIdsAsync(
            realmId,
            accountId,
            characterId,
            cancellationToken);
        var recovered = 0;
        foreach (var reservationId in reservationIds)
        {
            await using var connection =
                await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction =
                await connection.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            await LockReservationAsync(
                connection,
                transaction,
                reservationId,
                cancellationToken);
            if (!await LockAndValidateOwnershipAsync(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    ownership,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "Atlantis Opal login recovery lost character " +
                    "ownership.");
            }
            _ = await SettleLockedAsync(
                connection,
                transaction,
                reservationId,
                new HashSet<int>(),
                releaseClaimsWithoutPayments: false,
                restrictedCharacterIds: new HashSet<int> { characterId },
                takeoverCharacterId: characterId,
                takeoverOwnership: ownership,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            recovered++;
        }
        return recovered;
    }

    private async Task<IReadOnlyList<Guid>>
        ReadCharacterReservationIdsAsync(
            RealmId realmId,
            int accountId,
            int characterId,
            CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT reservation_id
            FROM public.legacy_instance_opal_payments
            WHERE realm_id = @realmId
              AND account_id = @accountId
              AND character_id = @characterId
              AND payment_status = 'pending'
            ORDER BY charged_at, reservation_id;
            """,
            connection)
        {
            CommandTimeout = _commandTimeoutSeconds
        };
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)realmId.Value));
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetGuid(0));
        }
        return result;
    }

    private async Task<bool> LockAndValidateOwnershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
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

    private async Task<IReadOnlyList<Guid>> ReadStaleReservationIdsAsync(
        RealmId realmId,
        DateTimeOffset staleBeforeUtc,
        CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT payment.reservation_id
            FROM public.legacy_instance_opal_payments payment
            JOIN public.character_base character
              ON character.id = payment.character_id
             AND character.account_id = payment.account_id
            WHERE payment.payment_status = 'pending'
              AND payment.realm_id = @realmId
              AND payment.charged_at < @staleBefore
              AND character.checkpoint_owner_id IS NULL
            GROUP BY payment.reservation_id
            ORDER BY payment.reservation_id;
            """,
            connection)
        {
            CommandTimeout = _commandTimeoutSeconds
        };
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)realmId.Value));
        command.Parameters.Add(
            "staleBefore",
            NpgsqlDbType.TimestampTz).Value =
            staleBeforeUtc.UtcDateTime;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetGuid(0));
        }
        return result;
    }

    private async Task<IReadOnlySet<int>>
        LockPendingCharacterIdsIfReservationOfflineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        RealmId realmId,
        DateTimeOffset staleBeforeUtc,
        CancellationToken cancellationToken)
    {
        var participantCharacterIds = new HashSet<int>();
        var reservationIsOffline = true;
        await using (var participantCommand = CreateCommand(
                         """
                         SELECT
                             entry.character_id,
                             entry.realm_id,
                             character.checkpoint_owner_id
                         FROM public.legacy_instance_daily_entries entry
                         JOIN public.character_base character
                           ON character.id = entry.character_id
                         WHERE entry.reservation_id = @reservationId
                         ORDER BY entry.character_id
                         FOR UPDATE OF character;
                         """,
                         connection,
                         transaction))
        {
            participantCommand.Parameters.AddWithValue(
                "reservationId",
                reservationId);
            await using var participantReader =
                await participantCommand.ExecuteReaderAsync(
                    cancellationToken);
            while (await participantReader.ReadAsync(cancellationToken))
            {
                participantCharacterIds.Add(participantReader.GetInt32(0));
                reservationIsOffline &=
                    participantReader.GetInt16(1) == realmId.Value &&
                    participantReader.IsDBNull(2);
            }
        }
        if (!reservationIsOffline || participantCharacterIds.Count == 0)
        {
            return new HashSet<int>();
        }

        await using var command = CreateCommand(
            """
            SELECT payment.character_id
            FROM public.legacy_instance_opal_payments payment
            WHERE payment.reservation_id = @reservationId
              AND payment.payment_status = 'pending'
              AND payment.realm_id = @realmId
              AND payment.charged_at < @staleBefore
              AND NOT EXISTS (
                  SELECT 1
                  FROM public.legacy_instance_opal_payments peer
                  WHERE peer.reservation_id = payment.reservation_id
                    AND peer.payment_status = 'pending'
                    AND (
                        peer.realm_id <> @realmId OR
                        peer.charged_at >= @staleBefore))
            ORDER BY payment.character_id
            FOR UPDATE OF payment;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)realmId.Value));
        command.Parameters.Add(
            "staleBefore",
            NpgsqlDbType.TimestampTz).Value =
            staleBeforeUtc.UtcDateTime;
        var result = new HashSet<int>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt32(0));
        }
        return result.Count != 0 && result.IsSubsetOf(participantCharacterIds)
            ? result
            : new HashSet<int>();
    }

    private async Task ReleaseAlreadyRefundedMemberClaimsAsync(
        RealmId realmId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM public.legacy_instance_daily_entries entry
            USING public.legacy_instance_opal_payments payment
            WHERE payment.reservation_id = entry.reservation_id
              AND payment.character_id = entry.character_id
              AND payment.payment_status = 'refunded'
              AND entry.admitted_at IS NULL
              AND payment.realm_id = @realmId;
            """,
            connection)
        {
            CommandTimeout = _commandTimeoutSeconds
        };
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)realmId.Value));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
