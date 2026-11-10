using System.Data;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceOpalPaymentStore :
    ILegacyInstanceOpalPaymentStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;

    public PostgresLegacyInstanceOpalPaymentStore(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            checked((int)Math.Ceiling(
                options.CommandTimeout.TotalSeconds)));
    }

    public async Task<LegacyInstanceOpalChargeResult> ChargeAsync(
        LegacyInstanceOpalChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var payers = request.Payers
            .OrderBy(static payer => payer.CharacterId)
            .ToArray();

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockReservationAsync(
            connection,
            transaction,
            request.ReservationId,
            cancellationToken);
        var existing = await ReadExistingChargeAsync(
            connection,
            transaction,
            request,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        await RequirePendingDailyEntriesAsync(
            connection,
            transaction,
            request,
            cancellationToken);
        var revisions = new Dictionary<int, long>(payers.Length);
        foreach (var payer in payers)
        {
            var subject = new CommandSubject(
                payer.AccountId,
                payer.CharacterId);
            var ownership = await _ownershipGuard.LockCurrentAsync(
                connection,
                transaction,
                subject,
                payer.Ownership,
                cancellationToken);
            if (ownership.Status != PlayerOwnershipValidationStatus.Current)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(
                    LegacyInstanceOpalChargeStatus.OwnershipLost,
                    new HashSet<int> { payer.CharacterId },
                    []);
            }
            if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                    connection,
                    transaction,
                    payer.AccountId,
                    payer.CharacterId,
                    _commandTimeoutSeconds,
                    cancellationToken))
            {
                throw new InvalidDataException(
                    "The Atlantis Opal payer has no economy baseline.");
            }
            revisions.Add(
                payer.CharacterId,
                await ReadInventoryRevisionAsync(
                    connection,
                    transaction,
                    payer,
                    cancellationToken));
        }

        var lockedItems = new Dictionary<int, LockedOpal>(payers.Length);
        var insufficient = new HashSet<int>();
        foreach (var payer in payers)
        {
            var item = await LockOpalAsync(
                connection,
                transaction,
                payer.CharacterId,
                cancellationToken);
            if (item is null)
            {
                insufficient.Add(payer.CharacterId);
            }
            else
            {
                lockedItems.Add(payer.CharacterId, item);
            }
        }
        if (insufficient.Count != 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(
                LegacyInstanceOpalChargeStatus.InsufficientOpal,
                insufficient,
                []);
        }

        var mutations = new List<LegacyInstanceOpalInventoryMutation>(
            payers.Length);
        foreach (var payer in payers)
        {
            var item = lockedItems[payer.CharacterId];
            var nextRevision = checked(revisions[payer.CharacterId] + 1);
            var mutation = await ConsumeOpalAsync(
                connection,
                transaction,
                payer,
                item,
                cancellationToken);
            var inboxId = await PersistEvidenceAsync(
                connection,
                transaction,
                request.ReservationId,
                payer.AccountId,
                payer.CharacterId,
                phase: "charge",
                nextRevision,
                item.ItemInstanceId,
                item.Slot,
                mutation.BeforeState,
                mutation.AfterState,
                cancellationToken);
            await AdvanceInventoryRevisionAsync(
                connection,
                transaction,
                payer.AccountId,
                payer.CharacterId,
                revisions[payer.CharacterId],
                nextRevision,
                cancellationToken);
            await InsertInventoryLedgerAsync(
                connection,
                transaction,
                inboxId,
                payer.AccountId,
                payer.CharacterId,
                nextRevision,
                item.ItemInstanceId,
                mutation.BeforeState,
                mutation.AfterState,
                "legacy_instance_opal_charge",
                cancellationToken);
            await InsertPaymentAsync(
                connection,
                transaction,
                request,
                payer,
                item,
                mutation,
                nextRevision,
                cancellationToken);
            mutations.Add(new(
                payer.AccountId,
                payer.CharacterId,
                item.Slot,
                item.BeforeCompact,
                item.AfterCompact));
        }

        await transaction.CommitAsync(cancellationToken);
        return new(
            LegacyInstanceOpalChargeStatus.Charged,
            new HashSet<int>(),
            mutations);
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private sealed record LockedOpal(
        long ItemInstanceId,
        short Slot,
        short Stack,
        string BeforeState,
        string BeforeCompact,
        string AfterCompact);

    private sealed record InventoryMutation(
        string BeforeState,
        string? AfterState);

    private sealed record PendingPayment(
        int AccountId,
        int CharacterId,
        PlayerOwnershipFence ChargeOwnership,
        long ItemInstanceId,
        short OriginalSlot,
        string BeforeState,
        string? AfterState,
        string BeforeCompact,
        string AfterCompact,
        bool IsAdmitted);
}
