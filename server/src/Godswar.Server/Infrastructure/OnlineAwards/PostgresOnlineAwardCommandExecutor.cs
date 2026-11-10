using Godswar.Server.Application.Pets;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Realms;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.Infrastructure.OnlineAwards;

internal sealed partial class PostgresOnlineAwardCommandExecutor :
    IOnlineAwardCommandExecutor
{
    private const int KitBagSlots = 96;
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly OnlineAwardBalanceSnapshot _balance;
    private readonly RealmCalendar _realmCalendar;
    private readonly string _itemContentRevision;

    public PostgresOnlineAwardCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        OnlineAwardBalanceSnapshot balance,
        RealmCalendar realmCalendar,
        IItemTemplateCatalog itemTemplates,
        IPetContentCatalog petContent)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts =
            checked((short)options.MaximumDeliveryAttempts);
        _balance = balance ??
            throw new ArgumentNullException(nameof(balance));
        _balance.Validate();
        _realmCalendar = realmCalendar ??
            throw new ArgumentNullException(nameof(realmCalendar));
        ArgumentNullException.ThrowIfNull(itemTemplates);
        _itemContentRevision = ValidateDigest(
            itemTemplates.Revision.Sha256);
        if (_balance.Rewards.Any(reward =>
                !OnlineAwardPinnedItemPolicy.IsValid(
                    itemTemplates,
                    petContent,
                    reward)))
        {
            throw new InvalidDataException(
                "The Online Award balance does not match pinned items.");
        }
    }

    public async Task<OnlineAwardExecutionResult> ExecuteAsync(
        CommandEnvelope<OnlineAwardCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation = OnlineAwardCommandEnvelope.Validate(envelope);
            if (validation == CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return OnlineAwardExecutionResult.Terminal(
                    OnlineAwardExecutionDisposition.RequestHashConflict);
            }
            if (validation != CommandEnvelopeValidation.Valid ||
                envelope.Command.RealmId != _realmCalendar.RealmId.Value ||
                _realmCalendar.GetDay(envelope.ReceivedAt).DayNumber !=
                    envelope.Command.ClaimDayNumber)
            {
                outcome = "invalid_intent";
                return OnlineAwardExecutionResult.Terminal(
                    OnlineAwardExecutionDisposition.InvalidIntent);
            }

            var result = await ExecuteTransactionAsync(
                envelope,
                cancellationToken);
            if (result.Receipt is not null)
            {
                (await _ownershipGuard.ValidateCurrentAsync(
                    envelope.Subject,
                    envelope.Ownership,
                    cancellationToken)).RequireCurrent();
            }
            outcome = result.Disposition.ToString().ToLowerInvariant();
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                OnlineAwardPersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<OnlineAwardExecutionResult> ExecuteTransactionAsync(
        CommandEnvelope<OnlineAwardCommand> envelope,
        CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey = OnlineAwardPersistenceCodec.AggregateKey(
            envelope.Subject.CharacterId);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var ownership = await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            envelope.Subject,
            envelope.Ownership,
            cancellationToken);
        if (ownership.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OnlineAwardExecutionResult.Terminal(
                OnlineAwardExecutionDisposition.PreconditionFailed);
        }
        ownership.RequireCurrent();

        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OnlineAwardExecutionResult.Terminal(
                OnlineAwardExecutionDisposition.PreconditionFailed);
        }

        var stored = await ReadInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            cancellationToken);
        if (stored is not null)
        {
            return await ReplayAsync(
                connection,
                transaction,
                envelope,
                stored,
                requestHash,
                cancellationToken);
        }

        var claimDay = DateOnly.FromDayNumber(
            envelope.Command.ClaimDayNumber);
        if (await HasDailyClaimAsync(
                connection,
                transaction,
                envelope,
                claimDay,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return OnlineAwardExecutionResult.Terminal(
                OnlineAwardExecutionDisposition.AlreadyClaimed);
        }

        var bag = await LockKitBagAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        var inventoryPlan = PlanInventory(bag, _balance.Rewards);
        if (inventoryPlan is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OnlineAwardExecutionResult.Terminal(
                OnlineAwardExecutionDisposition.BagFull);
        }

        var inventoryRevision = checked(character.Value.InventoryRevision + 1);
        var awardRevision = checked(character.Value.OnlineAwardRevision + 1);
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            operationId,
            requestHash,
            inventoryRevision,
            awardRevision,
            cancellationToken);
        var receipt = new OnlineAwardExecutionReceipt(
            envelope.Subject.CharacterId,
            envelope.Command.RealmId,
            claimDay,
            Godswar.Server.Domain.World.Content.OnlineAwardProtocol
                .SuccessSubId,
            _balance.Revision,
            _balance.Sha256,
            _itemContentRevision,
            _balance.Rewards.Select(static reward =>
                new OnlineAwardItemDelta(
                    reward.ItemId,
                    reward.ItemQuality,
                    reward.Bound,
                    reward.Quantity)).ToArray(),
            inventoryRevision,
            awardRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload = OnlineAwardPersistenceCodec.Encode(receipt);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            OnlineAwardPersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        var mutations = await ApplyInventoryAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            inventoryPlan,
            cancellationToken);
        await AdvanceCharacterRevisionsAsync(
            connection,
            transaction,
            envelope,
            character.Value,
            inventoryRevision,
            awardRevision,
            cancellationToken);
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            envelope,
            inventoryRevision,
            mutations,
            cancellationToken);
        await InsertSettlementAsync(
            connection,
            transaction,
            envelope,
            receipt,
            inboxId,
            auditId,
            payload,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            receipt,
            payload,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OnlineAwardExecutionResult.Terminal(
            OnlineAwardExecutionDisposition.Committed,
            receipt);
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private static byte[] DecodeDigest(string value)
    {
        var bytes = Convert.FromHexString(value);
        return bytes.Length == CommandEnvelopeContract.DigestBytes
            ? bytes
            : throw new InvalidDataException(
                "The Online Award command digest is invalid.");
    }

    private static string ValidateDigest(string value)
    {
        if (value.Length != 64 || value.Any(static character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "The item-content revision is invalid.",
                nameof(value));
        }
        return value;
    }

    private readonly record struct LockedCharacter(
        long InventoryRevision,
        long OnlineAwardRevision);

    private sealed record StoredInbox(
        long Id,
        byte[] RequestHash,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);
}
