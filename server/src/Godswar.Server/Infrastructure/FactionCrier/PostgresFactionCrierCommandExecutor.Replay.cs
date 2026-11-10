using System.Globalization;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Npgsql;

namespace Godswar.Server.Infrastructure.FactionCrier;

internal sealed partial class PostgresFactionCrierCommandExecutor
{
    public async Task<FactionCrierExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        FactionCrierReplayIntent replayIntent,
        FactionCrierOperationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (subject.AccountId <= 0 ||
            subject.CharacterId <= 0 ||
            !replayIntent.IsValid ||
            replayIntent.RealmId != _realmCalendar.RealmId.Value ||
            !identity.IsSecureClient)
        {
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.InvalidIntent);
        }

        var principalKey = subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey = FactionCrierPersistenceCodec.AggregateKey(
            subject.CharacterId);
        var operationId = DecodeDigest(
            FactionCrierCommandEnvelope.CreateOperationId(
                subject,
                identity));

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var ownershipResult = await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            subject,
            ownership,
            cancellationToken);
        if (ownershipResult.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.PreconditionFailed);
        }
        ownershipResult.RequireCurrent();

        if (await LockCharacterAsync(
                connection,
                transaction,
                subject.AccountId,
                subject.CharacterId,
                _realmCalendar.RealmId.Value,
                cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.PreconditionFailed);
        }

        var stored = await ReadInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            cancellationToken);
        if (stored is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.ReplayNotFound();
        }

        var receipt = FactionCrierPersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.AuditId);
        if (receipt.CharacterId != subject.CharacterId ||
            receipt.RealmId != replayIntent.RealmId ||
            receipt.Operation != replayIntent.Operation ||
            receipt.SubId != replayIntent.SubId)
        {
            await RecordRequestConflictAsync(
                connection,
                transaction,
                stored.Id,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.RequestHashConflict);
        }

        await RecordDuplicateAsync(
            connection,
            transaction,
            stored.Id,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        (await _ownershipGuard.ValidateCurrentAsync(
            subject,
            ownership,
            cancellationToken)).RequireCurrent();
        return FactionCrierExecutionResult.Terminal(
            FactionCrierExecutionDisposition.Duplicate,
            receipt);
    }
}
