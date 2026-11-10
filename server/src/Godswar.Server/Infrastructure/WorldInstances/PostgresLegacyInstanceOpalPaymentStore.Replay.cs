using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceOpalPaymentStore
{
    private async Task LockReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            "SELECT pg_advisory_xact_lock(" +
            "hashtextextended(@reservationId::text, 3932));",
            connection,
            transaction);
        command.Parameters.AddWithValue("reservationId", reservationId);
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<LegacyInstanceOpalChargeResult?>
        ReadExistingChargeAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            LegacyInstanceOpalChargeRequest request,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                realm_id, account_id, character_id,
                original_kitbag_slot, before_compact_state,
                after_compact_state, payment_status,
                charge_owner_id, charge_owner_generation
            FROM public.legacy_instance_opal_payments
            WHERE reservation_id = @reservationId
            ORDER BY character_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "reservationId",
            request.ReservationId);
        var mutations = new List<LegacyInstanceOpalInventoryMutation>(3);
        var statuses = new List<string>(3);
        var ownerships = new List<PlayerOwnershipFence>(3);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetInt16(0) != request.RealmId.Value)
            {
                throw new InvalidDataException(
                    "An Atlantis payment reservation changed realm.");
            }
            mutations.Add(new(
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt16(3),
                reader.GetString(4),
                reader.GetString(5)));
            statuses.Add(reader.GetString(6));
            ownerships.Add(new(
                reader.GetGuid(7),
                reader.GetInt64(8)));
        }
        if (mutations.Count == 0)
        {
            return null;
        }

        var expected = request.Payers
            .Select(static payer => (
                payer.AccountId,
                payer.CharacterId,
                payer.Ownership))
            .OrderBy(static payer => payer.CharacterId)
            .ToArray();
        var stored = mutations
            .Select((mutation, index) => (
                mutation.AccountId,
                mutation.CharacterId,
                ownerships[index]))
            .OrderBy(static payer => payer.CharacterId)
            .ToArray();
        if (!expected.SequenceEqual(stored))
        {
            throw new InvalidDataException(
                "An Atlantis payment reservation changed participants.");
        }
        if (statuses.Any(static status =>
                !status.Equals("pending", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "An Atlantis payment reservation is already settled.");
        }
        return new(
            LegacyInstanceOpalChargeStatus.Charged,
            new HashSet<int>(),
            mutations);
    }
}
