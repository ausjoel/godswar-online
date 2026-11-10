using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Realms;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.FactionCrier;

internal sealed partial class PostgresFactionCrierCommandExecutor :
    IFactionCrierCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly FactionCrierBalanceSnapshot _balance;
    private readonly RealmCalendar _realmCalendar;
    private readonly string _itemContentRevision;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;

    public PostgresFactionCrierCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        FactionCrierBalanceSnapshot balance,
        RealmCalendar realmCalendar,
        string itemContentRevision)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _balance = balance ?? throw new ArgumentNullException(nameof(balance));
        _balance.Validate();
        _realmCalendar = realmCalendar ??
            throw new ArgumentNullException(nameof(realmCalendar));
        _itemContentRevision = string.IsNullOrWhiteSpace(itemContentRevision)
            ? throw new ArgumentException(
                "The item-content revision is required.",
                nameof(itemContentRevision))
            : itemContentRevision;
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts =
            checked((short)options.MaximumDeliveryAttempts);
    }

    public async Task<FactionCrierExecutionResult> ExecuteAsync(
        CommandEnvelope<FactionCrierCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation = FactionCrierCommandEnvelope.Validate(envelope);
            if (validation == CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return FactionCrierExecutionResult.Terminal(
                    FactionCrierExecutionDisposition.RequestHashConflict);
            }
            if (validation != CommandEnvelopeValidation.Valid ||
                CommandEnvelopeContract.ValidateOwnership(envelope) !=
                    CommandEnvelopeValidation.Valid ||
                envelope.Command.RealmId !=
                    _realmCalendar.RealmId.Value)
            {
                outcome = "invalid_intent";
                return FactionCrierExecutionResult.Terminal(
                    FactionCrierExecutionDisposition.InvalidIntent);
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
            outcome = OutcomeCode(result.Disposition);
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
                FactionCrierPersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<FactionCrierExecutionResult> ExecuteTransactionAsync(
        CommandEnvelope<FactionCrierCommand> envelope,
        CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey = FactionCrierPersistenceCodec.AggregateKey(
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
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.PreconditionFailed);
        }
        ownership.RequireCurrent();

        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            _realmCalendar.RealmId.Value,
            cancellationToken);
        if (character is null)
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
        if (character.Value.Level < _balance.MinimumLevel)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.BelowMinimumLevel);
        }
        if (envelope.Command.Operation ==
                FactionCrierOperation.DailyClaim &&
            _realmCalendar.GetDay(envelope.ReceivedAt).DayOfWeek ==
                DayOfWeek.Sunday)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.ClosedToday);
        }
        if (!FactionCrierRewardPolicy.TryCreatePlan(
                envelope.Command,
                character.Value.Level,
                envelope.ReceivedAt,
                _balance,
                _realmCalendar,
                out var plan))
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.PreconditionFailed);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.PreconditionFailed);
        }

        var priorClaim = await HasPriorClaimAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.RealmId,
            plan,
            cancellationToken);
        if (priorClaim)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.AlreadyClaimed);
        }

        if (!TryDebitWallet(character.Value, plan, out var walletAfter))
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.InsufficientCurrency);
        }

        var bag = await LockKitBagAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        var inventoryPlan = PlanInventoryMutation(
            envelope.Command,
            plan,
            bag);
        if (inventoryPlan.Disposition !=
            FactionCrierExecutionDisposition.Committed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                inventoryPlan.Disposition);
        }

        var fighter = PlayerExperienceCatalog.Apply(
            character.Value.Level,
            character.Value.Experience,
            plan.AwardedExperience,
            character.Value.LevelSealed);
        int talentAfter;
        try
        {
            talentAfter = checked(
                character.Value.TalentPoints + plan.AwardedTalentPoints);
        }
        catch (OverflowException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.PreconditionFailed);
        }

        var revisions = DerivedRevisions.Create(character.Value, plan);
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            character.Value,
            plan,
            walletAfter,
            fighter,
            talentAfter,
            revisions,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = CreateReceipt(
            envelope,
            character.Value,
            plan,
            walletAfter,
            fighter,
            talentAfter,
            revisions,
            auditId,
            eventId);
        var payload = FactionCrierPersistenceCodec.Encode(receipt);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            FactionCrierPersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);

        var mutations = await ApplyInventoryMutationAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            inventoryPlan,
            cancellationToken);
        await UpdateCharacterAsync(
            connection,
            transaction,
            envelope,
            character.Value,
            walletAfter,
            fighter,
            talentAfter,
            revisions,
            cancellationToken);
        await InsertLedgersAsync(
            connection,
            transaction,
            inboxId,
            envelope,
            character.Value,
            plan,
            walletAfter,
            revisions,
            mutations,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            revisions.FactionCrier,
            eventId,
            payload,
            cancellationToken);
        await InsertSettlementAsync(
            connection,
            transaction,
            envelope,
            plan,
            receipt,
            revisions,
            inboxId,
            auditId,
            eventId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return FactionCrierExecutionResult.Terminal(
            FactionCrierExecutionDisposition.Committed,
            receipt);
    }

    private async Task<FactionCrierExecutionResult> ReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<FactionCrierCommand> envelope,
        StoredInbox stored,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                stored.RequestHash,
                requestHash))
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

        var receipt = FactionCrierPersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.AuditId);
        if (receipt.CharacterId != envelope.Subject.CharacterId ||
            receipt.RealmId != envelope.Command.RealmId ||
            receipt.Operation != envelope.Command.Operation ||
            receipt.SubId != envelope.Command.SubId)
        {
            throw new InvalidDataException(
                "The stored Faction Crier identity is inconsistent.");
        }

        await RecordDuplicateAsync(
            connection,
            transaction,
            stored.Id,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return FactionCrierExecutionResult.Terminal(
            FactionCrierExecutionDisposition.Duplicate,
            receipt);
    }

    private FactionCrierExecutionReceipt CreateReceipt(
        CommandEnvelope<FactionCrierCommand> envelope,
        LockedCharacter before,
        FactionCrierExecutionPlan plan,
        CharacterWalletSnapshot wallet,
        PlayerExperienceProgression fighter,
        int talentAfter,
        DerivedRevisions revisions,
        long auditId,
        Guid eventId) =>
        new(
            envelope.Subject.CharacterId,
            envelope.Command.RealmId,
            envelope.Command.Operation,
            envelope.Command.SubId,
            plan.NativeSuccessSubId,
            Succeeded: true,
            before.Level,
            fighter.Level,
            before.Experience,
            fighter.Experience,
            fighter.ExperienceGained,
            fighter.LevelUps.Select(static value => new FactionCrierLevelUp(
                value.Level,
                value.CurrentExperience,
                value.NextLevelExperience)).ToArray(),
            before.TalentPoints,
            talentAfter,
            plan.AwardedTalentPoints,
            wallet,
            revisions.Wallet,
            revisions.Inventory,
            revisions.Progression,
            revisions.FactionCrier,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);

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
                "The command digest has an invalid size.");
    }

    private static string OutcomeCode(
        FactionCrierExecutionDisposition disposition) =>
        disposition.ToString().ToLowerInvariant();

    private readonly record struct LockedCharacter(
        int Level,
        long Experience,
        bool LevelSealed,
        int TalentPoints,
        int Silver,
        int Gold,
        int BindingGold,
        long WalletRevision,
        long InventoryRevision,
        long ProgressionRevision,
        long FactionCrierRevision);

    private readonly record struct DerivedRevisions(
        long Wallet,
        long Inventory,
        long Progression,
        long FactionCrier)
    {
        public static DerivedRevisions Create(
            LockedCharacter before,
            FactionCrierExecutionPlan plan) =>
            new(
                before.WalletRevision + (plan.CurrencyCost > 0 ? 1 : 0),
                checked(before.InventoryRevision + 1),
                before.ProgressionRevision +
                    (plan.AwardedExperience > 0 ||
                     plan.AwardedTalentPoints > 0 ? 1 : 0),
                checked(before.FactionCrierRevision + 1));
    }

    private sealed record StoredInbox(
        long Id,
        byte[] RequestHash,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);
}
