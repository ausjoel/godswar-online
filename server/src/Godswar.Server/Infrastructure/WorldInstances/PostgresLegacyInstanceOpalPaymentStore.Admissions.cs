using System.Data;
using Godswar.Server.Application.WorldInstances;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceOpalPaymentStore
{
    public async Task RecordAdmissionsAsync(
        Guid reservationId,
        IReadOnlyCollection<int> admittedCharacterIds,
        CancellationToken cancellationToken = default)
    {
        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An Atlantis admission needs a reservation.",
                nameof(reservationId));
        }
        ArgumentNullException.ThrowIfNull(admittedCharacterIds);
        var admitted = admittedCharacterIds.ToHashSet();
        if (admitted.Count != admittedCharacterIds.Count ||
            admitted.Any(static id => id <= 0))
        {
            throw new ArgumentException(
                "Admitted Atlantis characters must be unique and positive.",
                nameof(admittedCharacterIds));
        }
        if (admitted.Count == 0)
        {
            return;
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockReservationAsync(
            connection,
            transaction,
            reservationId,
            cancellationToken);
        await using (var dailyCommand = CreateCommand(
            """
            UPDATE public.legacy_instance_daily_entries
            SET admitted_at = COALESCE(
                admitted_at,
                GREATEST(claimed_at, clock_timestamp()))
            WHERE reservation_id = @reservationId
              AND character_id = ANY(@characterIds);
            """,
            connection,
            transaction))
        {
            dailyCommand.Parameters.AddWithValue(
                "reservationId",
                reservationId);
            dailyCommand.Parameters.Add(
                "characterIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                admitted.ToArray();
            if (await dailyCommand.ExecuteNonQueryAsync(
                    cancellationToken) != admitted.Count)
            {
                throw new InvalidDataException(
                    "Atlantis admissions did not match the reserved " +
                    "daily entries exactly.");
            }
        }

        await using var command = CreateCommand(
            """
            UPDATE public.legacy_instance_opal_payments
            SET admitted_at = COALESCE(admitted_at, now())
            WHERE reservation_id = @reservationId
              AND character_id = ANY(@characterIds)
              AND payment_status IN ('pending', 'committed');
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            admitted.ToArray();
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
