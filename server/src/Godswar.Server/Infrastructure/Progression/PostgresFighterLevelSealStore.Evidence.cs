using Godswar.Server.State;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Progression;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed partial class PostgresFighterLevelSealStore
{
    private const string FighterLevelSealCommandFamily =
        "fighter_level_seal";

    private static FighterLevelSealRequestEvidence
        CreateFighterLevelSealRequest(
        RealmId realmId,
        bool desiredSealed)
    {
        var payload = JsonSerializer.Serialize(new
        {
            realmId = realmId.Value,
            desiredSealed
        });
        return new(
            payload,
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static async Task<FighterLevelSealStoredInbox?>
        ReadFighterLevelSealInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, request_hash, result_payload::text,
                   result_hash, audit_id
            FROM public.command_inbox
            WHERE principal_type = 'account'
              AND principal_key = @principalKey
              AND aggregate_type = 'character'
              AND aggregate_key = @aggregateKey
              AND command_family = @commandFamily
              AND operation_id = @operationId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "principalKey",
            accountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        command.Parameters.AddWithValue(
            "commandFamily",
            FighterLevelSealCommandFamily);
        command.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationId.ToByteArray();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new FighterLevelSealStoredInbox(
                reader.GetInt64(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                reader.GetInt64(4))
            : null;
    }

    private static async Task<FighterLevelSealChangeResult>
        ReplayFighterLevelSealAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        RealmId realmId,
        byte[] requestHash,
        FighterLevelSealStoredInbox stored,
        FighterLevelSealLockedCharacter current,
        CancellationToken cancellationToken)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                stored.RequestHash,
                requestHash))
        {
            await UpdateFighterLevelSealInboxEvidenceAsync(
                connection,
                transaction,
                stored.Id,
                duplicate: false,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                FighterLevelSealChangeStatus.RequestHashConflict,
                current.LevelSealed,
                current.BindingGold,
                current.SealRevision);
        }

        var result = JsonSerializer.Deserialize<FighterLevelSealStoredResult>(
            stored.ResultPayload) ??
            throw new InvalidDataException(
                "The stored Level Sealer result is invalid.");
        // PostgreSQL jsonb normalizes whitespace and property ordering when it
        // is read as text. Re-encode the typed result before checking the hash
        // so a valid replay is not rejected because of jsonb presentation.
        var canonicalResult = JsonSerializer.SerializeToUtf8Bytes(result);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(canonicalResult),
                stored.ResultHash))
        {
            throw new InvalidDataException(
                "The stored Level Sealer result hash is invalid.");
        }
        if (result.CharacterId != characterId ||
            result.RealmId != realmId.Value ||
            result.BindingGold < 0 ||
            result.SealRevision < 0 ||
            current.SealRevision < result.SealRevision ||
            result.Status is
                FighterLevelSealChangeStatus.CharacterNotFound or
                FighterLevelSealChangeStatus.RequestHashConflict ||
            !Enum.IsDefined(result.Status) ||
            stored.AuditId <= 0)
        {
            throw new InvalidDataException(
                "The stored Level Sealer command identity is inconsistent.");
        }
        await UpdateFighterLevelSealInboxEvidenceAsync(
            connection,
            transaction,
            stored.Id,
            duplicate: true,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            result.Status,
            result.LevelSealed,
            result.BindingGold,
            result.SealRevision,
            Replayed: true,
            ReplayProjection: new(
                current.LevelSealed,
                current.BindingGold,
                current.SealRevision));
    }

    private static async Task UpdateFighterLevelSealInboxEvidenceAsync(
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
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Level Sealer replay evidence was not exact.");
        }
    }

    private static async Task<long> InsertFighterLevelSealEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        RealmId realmId,
        Guid operationId,
        FighterLevelSealRequestEvidence request,
        FighterLevelSealChangeStatus status,
        FighterLevelSealLockedCharacter before,
        bool desiredSealed,
        int bindingGoldAfter,
        long walletRevision,
        long sealRevision,
        CancellationToken cancellationToken)
    {
        var levelSealedAfter = status is
            FighterLevelSealChangeStatus.Sealed or
            FighterLevelSealChangeStatus.Unsealed
                ? desiredSealed
                : before.LevelSealed;
        var storedResult = new FighterLevelSealStoredResult(
            characterId,
            realmId.Value,
            status,
            levelSealedAfter,
            bindingGoldAfter,
            sealRevision);
        var resultPayload = JsonSerializer.Serialize(storedResult);
        var resultHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(resultPayload));
        var operationBytes = operationId.ToByteArray();
        var principalKey = accountId.ToString(CultureInfo.InvariantCulture);
        var aggregateKey = $"character:{characterId}";
        var resultCode = FighterLevelSealResultCode(status);

        long auditId;
        await using (var audit = new NpgsqlCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, outcome_code, detail_payload)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    @commandFamily, @operationId,
                    @requestHash, @resultCode, @requestPayload)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            audit.Parameters.AddWithValue("principalKey", principalKey);
            audit.Parameters.AddWithValue("aggregateKey", aggregateKey);
            audit.Parameters.AddWithValue(
                "commandFamily",
                FighterLevelSealCommandFamily);
            audit.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
                operationBytes;
            audit.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
                request.Hash;
            audit.Parameters.AddWithValue("resultCode", resultCode);
            audit.Parameters.Add("requestPayload", NpgsqlDbType.Jsonb).Value =
                request.Payload;
            auditId = await audit.ExecuteScalarAsync(cancellationToken)
                is long value && value > 0
                ? value
                : throw new InvalidDataException(
                    "Level-seal audit returned no identity.");
        }

        await using var inbox = new NpgsqlCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, result_contract_version, result_code,
                result_payload, result_hash, audit_id)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    @commandFamily, @operationId,
                    @requestHash, 1, @resultCode, @resultPayload,
                    @resultHash, @auditId)
            RETURNING id;
            """,
            connection,
            transaction);
        inbox.Parameters.AddWithValue("principalKey", principalKey);
        inbox.Parameters.AddWithValue("aggregateKey", aggregateKey);
        inbox.Parameters.AddWithValue(
            "commandFamily",
            FighterLevelSealCommandFamily);
        inbox.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationBytes;
        inbox.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
            request.Hash;
        inbox.Parameters.AddWithValue("resultCode", resultCode);
        inbox.Parameters.Add("resultPayload", NpgsqlDbType.Jsonb).Value =
            resultPayload;
        inbox.Parameters.Add("resultHash", NpgsqlDbType.Bytea).Value =
            resultHash;
        inbox.Parameters.AddWithValue("auditId", auditId);
        return await inbox.ExecuteScalarAsync(cancellationToken)
            is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "Level-seal inbox returned no identity.");
    }

    private static string FighterLevelSealResultCode(
        FighterLevelSealChangeStatus status) => status switch
        {
            FighterLevelSealChangeStatus.Sealed => "sealed",
            FighterLevelSealChangeStatus.Unsealed => "unsealed",
            FighterLevelSealChangeStatus.AlreadySealed => "already_sealed",
            FighterLevelSealChangeStatus.AlreadyUnsealed =>
                "already_unsealed",
            FighterLevelSealChangeStatus.InsufficientBoundGold =>
                "insufficient_bound_gold",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private static async Task UpdateFighterLevelSealCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        RealmId realmId,
        bool desiredSealed,
        FighterLevelSealLockedCharacter before,
        int bindingGoldAfter,
        long walletRevision,
        long sealRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET fighter_level_sealed = @desiredSealed,
                "BindingGold" = @bindingGoldAfter,
                wallet_revision = @walletRevision,
                fighter_level_seal_revision = @sealRevision
            WHERE id = @characterId
              AND account_id = @accountId
              AND server_id = @realmId
              AND lifecycle_state = 'active'
              AND fighter_job_lv = @expectedLevel
              AND fighter_level_sealed = @expectedSealed
              AND "BindingGold" = @expectedBindingGold
              AND wallet_revision = @expectedWalletRevision
              AND progression_reward_revision =
                    @expectedProgressionRevision
              AND fighter_level_seal_revision = @expectedSealRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("desiredSealed", desiredSealed);
        command.Parameters.AddWithValue(
            "bindingGoldAfter",
            bindingGoldAfter);
        command.Parameters.AddWithValue("walletRevision", walletRevision);
        command.Parameters.AddWithValue("sealRevision", sealRevision);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue("expectedLevel", before.Level);
        command.Parameters.AddWithValue(
            "expectedSealed",
            before.LevelSealed);
        command.Parameters.AddWithValue(
            "expectedBindingGold",
            before.BindingGold);
        command.Parameters.AddWithValue(
            "expectedWalletRevision",
            before.WalletRevision);
        command.Parameters.AddWithValue(
            "expectedProgressionRevision",
            before.ProgressionRevision);
        command.Parameters.AddWithValue(
            "expectedSealRevision",
            before.SealRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Level-seal character state was not advanced once.");
        }
    }

    private static async Task InsertFighterLevelSealCurrencyLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        int accountId,
        int characterId,
        int balanceBefore,
        int balanceAfter,
        long walletRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_currency_ledger (
                command_inbox_id, account_id, character_id,
                wallet_revision, currency_code, delta,
                balance_before, balance_after, reason_code)
            VALUES (@inboxId, @accountId, @characterId,
                    @walletRevision, 'binding_gold', @delta,
                    @balanceBefore, @balanceAfter,
                    'fighter_level_unseal');
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("walletRevision", walletRevision);
        command.Parameters.AddWithValue(
            "delta",
            -(long)(balanceBefore - balanceAfter));
        command.Parameters.AddWithValue(
            "balanceBefore",
            (long)balanceBefore);
        command.Parameters.AddWithValue(
            "balanceAfter",
            (long)balanceAfter);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Level-unseal currency ledger was not inserted once.");
        }
    }

    private readonly record struct FighterLevelSealRequestEvidence(
        string Payload,
        byte[] Hash);

    private readonly record struct FighterLevelSealStoredInbox(
        long Id,
        byte[] RequestHash,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record FighterLevelSealStoredResult(
        int CharacterId,
        int RealmId,
        FighterLevelSealChangeStatus Status,
        bool LevelSealed,
        int BindingGold,
        long SealRevision);
}
