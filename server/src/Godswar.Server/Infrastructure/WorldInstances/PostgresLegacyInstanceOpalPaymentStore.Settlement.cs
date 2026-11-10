using System.Data;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceOpalPaymentStore
{
    public async Task<LegacyInstanceOpalSettlementResult> SettleAsync(
        Guid reservationId,
        IReadOnlyCollection<int> admittedCharacterIds,
        CancellationToken cancellationToken = default)
    {
        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An Atlantis Opal settlement needs a reservation.",
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
        var result = await SettleLockedAsync(
            connection,
            transaction,
            reservationId,
            admitted,
            releaseClaimsWithoutPayments: true,
            restrictedCharacterIds: null,
            takeoverCharacterId: null,
            takeoverOwnership: null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<LegacyInstanceOpalSettlementResult>
        SettleLockedAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid reservationId,
            IReadOnlySet<int> admitted,
            bool releaseClaimsWithoutPayments,
            IReadOnlySet<int>? restrictedCharacterIds,
            int? takeoverCharacterId,
            PlayerOwnershipFence? takeoverOwnership,
            CancellationToken cancellationToken)
    {
        await RecordDailyAdmissionsLockedAsync(
            connection,
            transaction,
            reservationId,
            admitted,
            cancellationToken);
        var pending = await LockPendingPaymentsAsync(
            connection,
            transaction,
            reservationId,
            cancellationToken);
        if (restrictedCharacterIds is not null)
        {
            pending = pending
                .Where(payment => restrictedCharacterIds.Contains(
                    payment.CharacterId))
                .ToArray();
        }
        var effectiveAdmitted = pending
            .Where(static payment => payment.IsAdmitted)
            .Select(static payment => payment.CharacterId)
            .ToHashSet();
        var refundMutations =
            new List<LegacyInstanceOpalInventoryMutation>();
        var refundedCharacterIds = new HashSet<int>();
        var deferredCharacterIds = new HashSet<int>();
        foreach (var payment in pending
            .Where(payment =>
                !effectiveAdmitted.Contains(payment.CharacterId))
            .OrderBy(static payment => payment.CharacterId))
        {
            var authority = await LockRefundAuthorityAsync(
                connection,
                transaction,
                payment,
                takeoverCharacterId,
                takeoverOwnership,
                cancellationToken);
            if (!authority.IsAuthorized)
            {
                deferredCharacterIds.Add(payment.CharacterId);
                continue;
            }
            var refund = await RefundOpalAsync(
                connection,
                transaction,
                payment,
                cancellationToken);
            var nextRevision = checked(authority.InventoryRevision + 1);
            var inboxId = await PersistEvidenceAsync(
                connection,
                transaction,
                reservationId,
                payment.AccountId,
                payment.CharacterId,
                phase: "refund",
                nextRevision,
                refund.ItemInstanceId,
                refund.Slot,
                refund.BeforeState,
                refund.AfterState,
                cancellationToken);
            await AdvanceInventoryRevisionAsync(
                connection,
                transaction,
                payment.AccountId,
                payment.CharacterId,
                authority.InventoryRevision,
                nextRevision,
                cancellationToken);
            await InsertInventoryLedgerAsync(
                connection,
                transaction,
                inboxId,
                payment.AccountId,
                payment.CharacterId,
                nextRevision,
                refund.ItemInstanceId,
                refund.BeforeState,
                refund.AfterState,
                "legacy_instance_opal_refund",
                cancellationToken);
            await MarkRefundedAsync(
                connection,
                transaction,
                reservationId,
                payment.CharacterId,
                nextRevision,
                refund.ItemInstanceId,
                refund.Slot,
                cancellationToken);
            refundMutations.Add(new(
                payment.AccountId,
                payment.CharacterId,
                refund.Slot,
                refund.BeforeCompact,
                refund.AfterCompact));
            refundedCharacterIds.Add(payment.CharacterId);
        }

        await MarkCommittedAsync(
            connection,
            transaction,
            reservationId,
            pending
                .Where(payment =>
                    effectiveAdmitted.Contains(payment.CharacterId))
                .Select(static payment => payment.CharacterId)
                .ToArray(),
            cancellationToken);
        await ReleaseNonAdmittedClaimsAsync(
            connection,
            transaction,
            reservationId,
            releaseClaimsWithoutPayments,
            refundedCharacterIds,
            deferredCharacterIds,
            restrictedCharacterIds,
            cancellationToken);
        return new(refundMutations);
    }

    private async Task RecordDailyAdmissionsLockedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        IReadOnlySet<int> admittedCharacterIds,
        CancellationToken cancellationToken)
    {
        if (admittedCharacterIds.Count == 0)
        {
            return;
        }
        await using var command = CreateCommand(
            """
            UPDATE public.legacy_instance_daily_entries
            SET admitted_at = COALESCE(
                admitted_at,
                GREATEST(claimed_at, clock_timestamp()))
            WHERE reservation_id = @reservationId
              AND character_id = ANY(@characterIds);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            admittedCharacterIds.ToArray();
        if (await command.ExecuteNonQueryAsync(cancellationToken) !=
            admittedCharacterIds.Count)
        {
            throw new InvalidDataException(
                "Settled Atlantis admissions did not match their daily " +
                "entries exactly.");
        }
    }

    private async Task ReleaseNonAdmittedClaimsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        bool releaseClaimsWithoutPayments,
        IReadOnlySet<int> refundedCharacterIds,
        IReadOnlySet<int> deferredCharacterIds,
        IReadOnlySet<int>? restrictedCharacterIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.legacy_instance_daily_entries
            WHERE reservation_id = @reservationId
              AND admitted_at IS NULL
              AND NOT (character_id = ANY(@deferredCharacterIds))
              AND (
                    character_id = ANY(@refundedCharacterIds)
                    OR (
                        @releaseClaimsWithoutPayments
                        AND NOT EXISTS (
                            SELECT 1
                            FROM public.legacy_instance_opal_payments payment
                            WHERE payment.reservation_id = @reservationId
                              AND payment.character_id =
                                  legacy_instance_daily_entries.character_id)))
              AND (
                    @allCharacters
                    OR character_id = ANY(@restrictedCharacterIds));
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.AddWithValue(
            "releaseClaimsWithoutPayments",
            releaseClaimsWithoutPayments);
        command.Parameters.AddWithValue(
            "allCharacters",
            restrictedCharacterIds is null);
        command.Parameters.Add(
            "refundedCharacterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            refundedCharacterIds.ToArray();
        command.Parameters.Add(
            "deferredCharacterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            deferredCharacterIds.ToArray();
        command.Parameters.Add(
            "restrictedCharacterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            restrictedCharacterIds?.ToArray() ?? [];
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<PendingPayment>>
        LockPendingPaymentsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid reservationId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                account_id, character_id, item_instance_id,
                original_kitbag_slot, before_state::text,
                after_state::text, before_compact_state,
                after_compact_state,
                charge_owner_id, charge_owner_generation,
                EXISTS (
                    SELECT 1
                    FROM public.legacy_instance_daily_entries entry
                    WHERE entry.reservation_id = payment.reservation_id
                      AND entry.character_id = payment.character_id
                      AND entry.admitted_at IS NOT NULL)
            FROM public.legacy_instance_opal_payments payment
            WHERE payment.reservation_id = @reservationId
              AND payment.payment_status = 'pending'
            ORDER BY payment.character_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reservationId", reservationId);
        var result = new List<PendingPayment>(3);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetInt32(0),
                reader.GetInt32(1),
                new PlayerOwnershipFence(
                    reader.GetGuid(8),
                    reader.GetInt64(9)),
                reader.GetInt64(2),
                reader.GetInt16(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetBoolean(10)));
        }
        return result;
    }

    private async Task<RefundAuthority> LockRefundAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PendingPayment payment,
        int? takeoverCharacterId,
        PlayerOwnershipFence? takeoverOwnership,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                inventory_revision,
                checkpoint_owner_id,
                checkpoint_owner_generation
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", payment.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            payment.CharacterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The Atlantis Opal refund character disappeared.");
        }
        var inventoryRevision = reader.GetInt64(0);
        var currentOwnership = reader.IsDBNull(1)
            ? (PlayerOwnershipFence?)null
            : new PlayerOwnershipFence(
                reader.GetGuid(1),
                reader.GetInt64(2));
        var takeoverAuthorized =
            takeoverCharacterId == payment.CharacterId &&
            takeoverOwnership is { } takeover &&
            currentOwnership == takeover;
        var chargeOwnerAuthorized =
            currentOwnership is null ||
            currentOwnership == payment.ChargeOwnership;
        return new(
            takeoverAuthorized || chargeOwnerAuthorized,
            inventoryRevision);
    }

    private async Task MarkRefundedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        int characterId,
        long inventoryRevision,
        long refundItemInstanceId,
        short refundSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.legacy_instance_opal_payments
            SET payment_status = 'refunded',
                refund_inventory_revision = @inventoryRevision,
                refund_item_instance_id = @refundItemInstanceId,
                refund_kitbag_slot = @refundSlot,
                settled_at = now()
            WHERE reservation_id = @reservationId
              AND character_id = @characterId
              AND payment_status = 'pending';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "refundItemInstanceId",
            refundItemInstanceId);
        command.Parameters.AddWithValue("refundSlot", refundSlot);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Atlantis Opal refund was not settled exactly once.");
        }
    }

    private async Task MarkCommittedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        IReadOnlyCollection<int> characterIds,
        CancellationToken cancellationToken)
    {
        if (characterIds.Count == 0)
        {
            return;
        }
        await using var command = CreateCommand(
            """
            UPDATE public.legacy_instance_opal_payments
            SET payment_status = 'committed',
                admitted_at = COALESCE(admitted_at, now()),
                settled_at = now()
            WHERE reservation_id = @reservationId
              AND character_id = ANY(@characterIds)
              AND payment_status = 'pending';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            characterIds.ToArray();
        if (await command.ExecuteNonQueryAsync(cancellationToken) !=
            characterIds.Count)
        {
            throw new InvalidDataException(
                "The admitted Atlantis Opal payments were not committed " +
                "exactly once.");
        }
    }

    private sealed record RefundAuthority(
        bool IsAuthorized,
        long InventoryRevision);
}
