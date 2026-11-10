using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.FactionCrier;

internal sealed partial class PostgresFactionCrierCommandExecutor
{
    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<FactionCrierCommand> envelope,
        LockedCharacter before,
        FactionCrierExecutionPlan plan,
        CharacterWalletSnapshot wallet,
        PlayerExperienceProgression fighter,
        int talentAfter,
        DerivedRevisions revisions,
        byte[] operationId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var detail = JsonSerializer.Serialize(new
        {
            realmId = envelope.Command.RealmId,
            npcId = envelope.Command.NpcId,
            dialogIndex = envelope.Command.DialogIndex,
            operation = envelope.Command.Operation.ToString(),
            subId = envelope.Command.SubId,
            nativeResultSubId = plan.NativeSuccessSubId,
            consumedItemIds = plan.ConsumedItemIds,
            grantedItemId = plan.GrantedItemId,
            currency = plan.Currency.ToString(),
            currencyCost = plan.CurrencyCost,
            awardedExperience = fighter.ExperienceGained,
            awardedTalentPoints = plan.AwardedTalentPoints,
            previousLevel = before.Level,
            currentLevel = fighter.Level,
            previousExperience = before.Experience,
            currentExperience = fighter.Experience,
            previousTalentPoints = before.TalentPoints,
            currentTalentPoints = talentAfter,
            previousSilver = before.Silver,
            currentSilver = wallet.Silver,
            previousGold = before.Gold,
            currentGold = wallet.Gold,
            previousBindingGold = before.BindingGold,
            currentBindingGold = wallet.BindingGold,
            balanceRevision = _balance.Revision,
            itemContentRevision = _itemContentRevision,
            walletRevision = revisions.Wallet,
            inventoryRevision = revisions.Inventory,
            progressionRevision = revisions.Progression,
            factionCrierRevision = revisions.FactionCrier
        });
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_audit (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                outcome_code,
                detail_payload,
                retention_policy
            )
            VALUES (
                @principalType,
                @principalKey,
                @aggregateType,
                @aggregateKey,
                @commandFamily,
                @operationId,
                @requestHash,
                @outcomeCode,
                @detailPayload,
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(command, envelope, operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            FactionCrierPersistenceCodec.ResultCode);
        command.Parameters.Add(
            "detailPayload",
            NpgsqlDbType.Jsonb).Value = detail;
        command.Parameters.AddWithValue(
            "retentionPolicy",
            FactionCrierPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The Faction Crier audit insert returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        byte[] resultHash,
        long auditId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                result_contract_version,
                result_code,
                result_payload,
                result_hash,
                audit_id,
                retention_policy
            )
            VALUES (
                @principalType,
                @principalKey,
                @aggregateType,
                @aggregateKey,
                @commandFamily,
                @operationId,
                @requestHash,
                @contractVersion,
                @resultCode,
                @resultPayload,
                @resultHash,
                @auditId,
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "principalType",
            FactionCrierPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            FactionCrierPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            FactionCrierPersistenceCodec.CommandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "contractVersion",
            FactionCrierPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            FactionCrierPersistenceCodec.ResultCode);
        command.Parameters.Add(
            "resultPayload",
            NpgsqlDbType.Jsonb).Value = Encoding.UTF8.GetString(payload);
        command.Parameters.Add(
            "resultHash",
            NpgsqlDbType.Bytea).Value = resultHash;
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            FactionCrierPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The Faction Crier inbox insert returned no identity.");
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        string aggregateKey,
        long aggregateRevision,
        Guid eventId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id,
                command_inbox_id,
                consumer_key,
                aggregate_type,
                aggregate_key,
                aggregate_version,
                event_type,
                contract_version,
                ordering_policy,
                payload,
                max_attempts
            )
            VALUES (
                @eventId,
                @inboxId,
                @consumerKey,
                @aggregateType,
                @aggregateKey,
                @aggregateRevision,
                @eventType,
                @contractVersion,
                @orderingPolicy,
                @payload,
                @maxAttempts
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            FactionCrierPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            FactionCrierPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateRevision",
            aggregateRevision);
        command.Parameters.AddWithValue(
            "eventType",
            FactionCrierPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            FactionCrierPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            FactionCrierPersistenceCodec.OrderingPolicy);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value = Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Faction Crier outbox insert was not exact.");
        }
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        CommandEnvelope<FactionCrierCommand> envelope,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            FactionCrierPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            envelope.Subject.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            FactionCrierPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            FactionCrierPersistenceCodec.AggregateKey(
                envelope.Subject.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            FactionCrierPersistenceCodec.CommandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
