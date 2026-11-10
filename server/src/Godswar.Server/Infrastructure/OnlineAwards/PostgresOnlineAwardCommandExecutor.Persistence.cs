using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.OnlineAwards;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.OnlineAwards;

internal sealed partial class PostgresOnlineAwardCommandExecutor
{
    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<OnlineAwardCommand> envelope,
        byte[] operationId,
        byte[] requestHash,
        long inventoryRevision,
        long awardRevision,
        CancellationToken cancellationToken)
    {
        var detail = JsonSerializer.Serialize(new
        {
            realmId = envelope.Command.RealmId,
            npcId = envelope.Command.NpcId,
            dialogIndex = envelope.Command.DialogIndex,
            claimDay = DateOnly.FromDayNumber(
                envelope.Command.ClaimDayNumber),
            balanceRevision = _balance.Revision,
            balanceSha256 = _balance.Sha256,
            itemContentRevision = _itemContentRevision,
            rewards = _balance.Rewards,
            inventoryRevision,
            onlineAwardRevision = awardRevision
        });
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key,
                aggregate_type, aggregate_key,
                command_family, operation_id, request_hash,
                outcome_code, detail_payload, retention_policy)
            VALUES (@principalType, @principalKey,
                    @aggregateType, @aggregateKey,
                    @commandFamily, @operationId, @requestHash,
                    @outcomeCode, @detailPayload, @retentionPolicy)
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            envelope.Subject.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            OnlineAwardPersistenceCodec.AggregateKey(
                envelope.Subject.CharacterId),
            operationId);
        command.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
            requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            OnlineAwardPersistenceCodec.ResultCode);
        command.Parameters.Add("detailPayload", NpgsqlDbType.Jsonb).Value =
            detail;
        command.Parameters.AddWithValue(
            "retentionPolicy",
            OnlineAwardPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The Online Award audit insert returned no identity.");
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
                principal_type, principal_key,
                aggregate_type, aggregate_key,
                command_family, operation_id, request_hash,
                result_contract_version, result_code,
                result_payload, result_hash, audit_id, retention_policy)
            VALUES (@principalType, @principalKey,
                    @aggregateType, @aggregateKey,
                    @commandFamily, @operationId, @requestHash,
                    @contractVersion, @resultCode,
                    @resultPayload, @resultHash, @auditId, @retentionPolicy)
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
            requestHash;
        command.Parameters.AddWithValue(
            "contractVersion",
            OnlineAwardPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            OnlineAwardPersistenceCodec.ResultCode);
        command.Parameters.Add("resultPayload", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.Add("resultHash", NpgsqlDbType.Bytea).Value =
            resultHash;
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            OnlineAwardPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The Online Award inbox insert returned no identity.");
    }

    private async Task AdvanceCharacterRevisionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<OnlineAwardCommand> envelope,
        LockedCharacter before,
        long inventoryRevision,
        long awardRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = @inventoryRevision,
                online_award_revision = @awardRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND server_id = @realmId
              AND inventory_revision = @expectedInventory
              AND online_award_revision = @expectedAward;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        command.Parameters.AddWithValue("awardRevision", awardRevision);
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue("realmId", envelope.Command.RealmId);
        command.Parameters.AddWithValue(
            "expectedInventory",
            before.InventoryRevision);
        command.Parameters.AddWithValue(
            "expectedAward",
            before.OnlineAwardRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Online Award revisions did not advance exactly once.");
        }
    }

    private async Task InsertSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<OnlineAwardCommand> envelope,
        OnlineAwardExecutionReceipt receipt,
        long inboxId,
        long auditId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(payload);
        var itemDeltas = document.RootElement
            .GetProperty("itemDeltas")
            .GetRawText();
        await using var command = CreateCommand(
            """
            INSERT INTO public.online_award_claim_settlements (
                realm_id, account_id, character_id, claim_day,
                balance_revision, balance_sha256, item_content_revision,
                item_deltas, inventory_revision, online_award_revision,
                command_inbox_id, audit_id, event_id)
            VALUES (@realmId, @accountId, @characterId, @claimDay,
                    @balanceRevision, @balanceSha256, @itemRevision,
                    @itemDeltas, @inventoryRevision, @awardRevision,
                    @inboxId, @auditId, @eventId);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("realmId", receipt.RealmId);
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            receipt.CharacterId);
        command.Parameters.AddWithValue("claimDay", receipt.ClaimDay);
        command.Parameters.AddWithValue(
            "balanceRevision",
            receipt.BalanceRevision);
        command.Parameters.AddWithValue(
            "balanceSha256",
            receipt.BalanceSha256);
        command.Parameters.AddWithValue(
            "itemRevision",
            receipt.ItemContentRevision);
        command.Parameters.Add("itemDeltas", NpgsqlDbType.Jsonb).Value =
            itemDeltas;
        command.Parameters.AddWithValue(
            "inventoryRevision",
            receipt.InventoryRevision);
        command.Parameters.AddWithValue(
            "awardRevision",
            receipt.OnlineAwardRevision);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue("eventId", receipt.EventId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Online Award settlement insert was not exact.");
        }
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        string aggregateKey,
        OnlineAwardExecutionReceipt receipt,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id, command_inbox_id, consumer_key,
                aggregate_type, aggregate_key, aggregate_version,
                event_type, contract_version, ordering_policy,
                payload, max_attempts)
            VALUES (@eventId, @inboxId, @consumerKey,
                    @aggregateType, @aggregateKey, @aggregateVersion,
                    @eventType, @contractVersion, @orderingPolicy,
                    @payload, @maxAttempts);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", receipt.EventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            OnlineAwardPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            OnlineAwardPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            receipt.OnlineAwardRevision);
        command.Parameters.AddWithValue(
            "eventType",
            OnlineAwardPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            OnlineAwardPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            OnlineAwardPersistenceCodec.OrderingPolicy);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Online Award outbox insert was not exact.");
        }
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            OnlineAwardPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            OnlineAwardPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            OnlineAwardPersistenceCodec.CommandFamily);
        command.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationId;
    }
}
