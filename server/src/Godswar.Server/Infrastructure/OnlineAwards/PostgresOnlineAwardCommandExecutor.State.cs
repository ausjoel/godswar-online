using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.OnlineAwards;
using Npgsql;

namespace Godswar.Server.Infrastructure.OnlineAwards;

internal sealed partial class PostgresOnlineAwardCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<OnlineAwardCommand> envelope,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT inventory_revision, online_award_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
              AND server_id = @realmId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue("realmId", envelope.Command.RealmId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var inventory = reader.GetInt64(0);
        var award = reader.GetInt64(1);
        return inventory >= 0 && award >= 0
            ? new LockedCharacter(inventory, award)
            : throw new InvalidDataException(
                "The Online Award character revisions are invalid.");
    }

    private async Task<bool> HasDailyClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<OnlineAwardCommand> envelope,
        DateOnly claimDay,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT 1
            FROM public.online_award_claim_settlements
            WHERE realm_id = @realmId
              AND character_id = @characterId
              AND claim_day = @claimDay;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("realmId", envelope.Command.RealmId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue("claimDay", claimDay);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<StoredInbox?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, request_hash, result_payload::text,
                   result_hash, audit_id
            FROM public.command_inbox
            WHERE principal_type = @principalType
              AND principal_key = @principalKey
              AND aggregate_type = @aggregateType
              AND aggregate_key = @aggregateKey
              AND command_family = @commandFamily
              AND operation_id = @operationId;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            principalKey,
            aggregateKey,
            operationId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredInbox(
                reader.GetInt64(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                reader.GetInt64(4))
            : null;
    }

    private async Task<OnlineAwardExecutionResult> ReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<OnlineAwardCommand> envelope,
        StoredInbox stored,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                stored.RequestHash,
                requestHash))
        {
            await UpdateInboxEvidenceAsync(
                connection,
                transaction,
                stored.Id,
                duplicate: false,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OnlineAwardExecutionResult.Terminal(
                OnlineAwardExecutionDisposition.RequestHashConflict);
        }

        var receipt = OnlineAwardPersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.AuditId);
        if (receipt.CharacterId != envelope.Subject.CharacterId ||
            receipt.RealmId != envelope.Command.RealmId ||
            receipt.ClaimDay.DayNumber != envelope.Command.ClaimDayNumber)
        {
            throw new InvalidDataException(
                "The stored Online Award command identity is inconsistent.");
        }
        await UpdateInboxEvidenceAsync(
            connection,
            transaction,
            stored.Id,
            duplicate: true,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OnlineAwardExecutionResult.Terminal(
            OnlineAwardExecutionDisposition.Duplicate,
            receipt);
    }

    private async Task UpdateInboxEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        bool duplicate,
        CancellationToken cancellationToken)
    {
        var sql = duplicate
            ? """
              UPDATE public.command_inbox
              SET duplicate_count = LEAST(duplicate_count + 1, 1000000),
                  last_duplicate_at = now()
              WHERE id = @inboxId;
              """
            : """
              UPDATE public.command_inbox
              SET request_conflict_count =
                      LEAST(request_conflict_count + 1, 1000000),
                  last_request_conflict_at = now()
              WHERE id = @inboxId;
              """;
        await using var command = CreateCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Online Award replay evidence was not exact.");
        }
    }
}
