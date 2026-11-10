using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceOpalPaymentStore
{
    private const string CommandFamily =
        "legacy_instance_opal_payment";

    private async Task<long> PersistEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        int accountId,
        int characterId,
        string phase,
        long inventoryRevision,
        long itemInstanceId,
        short slot,
        string? beforeState,
        string? afterState,
        CancellationToken cancellationToken)
    {
        var operationId = Hash(
            $"{CommandFamily}|{reservationId:N}|{characterId}|{phase}");
        var requestHash = Hash(
            $"{reservationId:N}|{accountId}|{characterId}|" +
            $"{itemInstanceId}|{slot}|{beforeState}|{afterState}");
        var resultCode = phase == "charge" ? "charged" : "refunded";
        var payload = JsonSerializer.Serialize(new
        {
            reservationId,
            accountId,
            characterId,
            phase,
            inventoryRevision,
            itemInstanceId,
            kitBagSlot = slot,
            itemTemplateId =
                LegacyInstanceOpalPaymentPolicy.OpalItemTemplateId,
            quantity = LegacyInstanceOpalPaymentPolicy
                .OpalsPerRetryingCharacter
        });
        var resultHash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        long auditId;
        await using (var audit = CreateCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, outcome_code, detail_payload,
                retention_policy)
            VALUES (
                'account', @principalKey, 'character', @aggregateKey,
                @commandFamily, @operationId, @requestHash, @resultCode,
                @payload, 'permanent')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            AddEvidenceParameters(
                audit,
                accountId,
                characterId,
                operationId,
                requestHash,
                resultCode,
                payload);
            auditId = await audit.ExecuteScalarAsync(cancellationToken)
                is long value && value > 0
                    ? value
                    : throw new InvalidDataException(
                        "The Atlantis Opal audit returned no identity.");
        }

        await using var inbox = CreateCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, result_contract_version, result_code,
                result_payload, result_hash, audit_id, retention_policy)
            VALUES (
                'account', @principalKey, 'character', @aggregateKey,
                @commandFamily, @operationId, @requestHash, 1,
                @resultCode, @payload, @resultHash, @auditId,
                'permanent')
            RETURNING id;
            """,
            connection,
            transaction);
        AddEvidenceParameters(
            inbox,
            accountId,
            characterId,
            operationId,
            requestHash,
            resultCode,
            payload);
        inbox.Parameters.Add(
            "resultHash",
            NpgsqlDbType.Bytea).Value = resultHash;
        inbox.Parameters.AddWithValue("auditId", auditId);
        return await inbox.ExecuteScalarAsync(cancellationToken)
            is long inboxId && inboxId > 0
                ? inboxId
                : throw new InvalidDataException(
                    "The Atlantis Opal inbox returned no identity.");
    }

    private static void AddEvidenceParameters(
        NpgsqlCommand command,
        int accountId,
        int characterId,
        byte[] operationId,
        byte[] requestHash,
        string resultCode,
        string payload)
    {
        command.Parameters.AddWithValue(
            "principalKey",
            accountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        command.Parameters.AddWithValue("commandFamily", CommandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue("resultCode", resultCode);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value = payload;
    }

    private async Task AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        long expectedRevision,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = @nextRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND inventory_revision = @expectedRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("expectedRevision", expectedRevision);
        command.Parameters.AddWithValue("nextRevision", nextRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Atlantis Opal inventory revision did not advance.");
        }
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        int accountId,
        int characterId,
        long inventoryRevision,
        long itemInstanceId,
        string? beforeState,
        string? afterState,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var mutationKind = beforeState is null
            ? "add"
            : afterState is null
                ? "delete"
                : "update";
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id, account_id, character_id,
                inventory_revision, entry_ordinal, item_instance_id,
                mutation_kind, state_contract_version, before_state,
                after_state, reason_code)
            VALUES (
                @inboxId, @accountId, @characterId, @inventoryRevision,
                0, @itemInstanceId, @mutationKind, 1, @beforeState,
                @afterState, @reasonCode);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        command.Parameters.AddWithValue("itemInstanceId", itemInstanceId);
        command.Parameters.AddWithValue("mutationKind", mutationKind);
        command.Parameters.Add(
            "beforeState",
            NpgsqlDbType.Jsonb).Value = beforeState is null
                ? DBNull.Value
                : beforeState;
        command.Parameters.Add(
            "afterState",
            NpgsqlDbType.Jsonb).Value = afterState is null
                ? DBNull.Value
                : afterState;
        command.Parameters.AddWithValue("reasonCode", reasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Atlantis Opal inventory ledger append was not exact.");
        }
    }

    private async Task InsertPaymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LegacyInstanceOpalChargeRequest request,
        LegacyInstanceOpalPayer payer,
        LockedOpal item,
        InventoryMutation mutation,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.legacy_instance_opal_payments (
                reservation_id, realm_id, account_id, character_id,
                item_instance_id, original_kitbag_slot, quantity,
                before_state, after_state, before_compact_state,
                after_compact_state, charge_inventory_revision,
                charge_owner_id, charge_owner_generation,
                payment_status, charged_at)
            VALUES (
                @reservationId, @realmId, @accountId, @characterId,
                @itemInstanceId, @slot, 1, @beforeState, @afterState,
                @beforeCompact, @afterCompact, @inventoryRevision,
                @chargeOwnerId, @chargeOwnerGeneration,
                'pending', @chargedAt);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "reservationId",
            request.ReservationId);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)request.RealmId.Value));
        command.Parameters.AddWithValue("accountId", payer.AccountId);
        command.Parameters.AddWithValue("characterId", payer.CharacterId);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            item.ItemInstanceId);
        command.Parameters.AddWithValue("slot", item.Slot);
        command.Parameters.Add(
            "beforeState",
            NpgsqlDbType.Jsonb).Value = mutation.BeforeState;
        command.Parameters.Add(
            "afterState",
            NpgsqlDbType.Jsonb).Value = mutation.AfterState is null
                ? DBNull.Value
                : mutation.AfterState;
        command.Parameters.AddWithValue(
            "beforeCompact",
            item.BeforeCompact);
        command.Parameters.AddWithValue("afterCompact", item.AfterCompact);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "chargeOwnerId",
            payer.Ownership.OwnerId);
        command.Parameters.AddWithValue(
            "chargeOwnerGeneration",
            payer.Ownership.Generation);
        command.Parameters.Add(
            "chargedAt",
            NpgsqlDbType.TimestampTz).Value =
            request.ChargedAtUtc.UtcDateTime;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Atlantis Opal payment was not recorded.");
        }
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
