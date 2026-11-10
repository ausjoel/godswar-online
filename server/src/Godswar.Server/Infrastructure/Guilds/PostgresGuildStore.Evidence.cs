using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// The durable evidence a guild creation leaves behind when it spends the
/// founder's Guild Stone.
/// </summary>
/// <remarks>
/// Spending an item is an inventory mutation, so it carries the same records
/// every other consumer in this repository writes: an audit row, a permanent
/// inbox row keyed by an operation id, the character's advanced inventory
/// revision, and the inventory ledger entry that makes the deletion replayable
/// and reconcilable. All of it happens inside the caller's transaction, so a
/// refused creation never spends the stone and a successful one can never be
/// missing its evidence.
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    private const string CreateCommandFamily = "guild_create";

    private const string GuildStoneLedgerReason = "guild_creation_stone";

    /// <summary>What the create spent, and where it was.</summary>
    private readonly record struct GuildStoneConsumption(
        long ItemInstanceId,
        short KitBagSlot,
        string BeforeState,
        string? AfterState);

    /// <summary>
    /// Takes one Guild Stone out of the founder's kit bag and records it.
    /// </summary>
    /// <returns>The spent stone, or null when the bag holds none.</returns>
    private static async Task<GuildStoneConsumption?>
        TryConsumeGuildStoneAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int accountId,
            int characterId,
            string guildName,
            CancellationToken cancellationToken)
    {
        long itemInstanceId;
        int stack;
        short slot;
        string beforeState;
        await using (var select = new NpgsqlCommand(
            """
            SELECT id, stack, slot_index, to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @itemId
            ORDER BY slot_index
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue("characterId", characterId);
            select.Parameters.AddWithValue("itemId", GuildStoneItemId);
            await using var reader = await select.ExecuteReaderAsync(
                cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            itemInstanceId = reader.GetInt64(0);
            stack = reader.GetInt32(1);
            slot = reader.GetInt16(2);
            beforeState = reader.GetString(3);
        }

        var afterState = stack > 1
            ? await DecrementStackAsync(
                connection,
                transaction,
                characterId,
                itemInstanceId,
                slot,
                stack,
                cancellationToken)
            : await DeleteStoneAsync(
                connection,
                transaction,
                characterId,
                itemInstanceId,
                slot,
                cancellationToken);

        var revision = await ReadInventoryRevisionAsync(
            connection,
            transaction,
            accountId,
            characterId,
            cancellationToken);
        var nextRevision = checked(revision + 1);
        var inboxId = await PersistEvidenceAsync(
            connection,
            transaction,
            accountId,
            characterId,
            guildName,
            nextRevision,
            itemInstanceId,
            slot,
            beforeState,
            afterState,
            cancellationToken);
        await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            accountId,
            characterId,
            revision,
            nextRevision,
            cancellationToken);
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            accountId,
            characterId,
            nextRevision,
            itemInstanceId,
            beforeState,
            afterState,
            cancellationToken);

        return new GuildStoneConsumption(
            itemInstanceId,
            slot,
            beforeState,
            afterState);
    }

    private static async Task<string?> DecrementStackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long itemInstanceId,
        short slot,
        int expectedStack,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_items
            SET stack = stack - 1,
                updated_at = now()
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @slot
              AND prop_id = @itemId
              AND stack = @expectedStack
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        AddStoneIdentityParameters(
            command,
            characterId,
            itemInstanceId,
            slot,
            expectedStack);
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException(
                "The locked Guild Stone stack was not decremented.");
    }

    private static async Task<string?> DeleteStoneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long itemInstanceId,
        short slot,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH deleted AS (
                DELETE FROM public.character_items
                WHERE id = @itemInstanceId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @slot
                  AND prop_id = @itemId
                  AND stack = 1
                RETURNING *
            )
            INSERT INTO public.character_item_audit (
                source, action, user_id, item_location, slot_index,
                prop_id, item_quality, item_grade, item_exp, old_item)
            SELECT
                'guild-registrar', 'delete', user_id, item_location,
                slot_index, prop_id, item_quality, item_grade, item_exp,
                to_jsonb(deleted)
            FROM deleted;
            """,
            connection,
            transaction);
        AddStoneIdentityParameters(
            command,
            characterId,
            itemInstanceId,
            slot,
            expectedStack: 1);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The locked Guild Stone was not deleted.");
        }

        return null;
    }

    private static void AddStoneIdentityParameters(
        NpgsqlCommand command,
        int characterId,
        long itemInstanceId,
        short slot,
        int expectedStack)
    {
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemInstanceId", itemInstanceId);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue("itemId", GuildStoneItemId);
        command.Parameters.AddWithValue("expectedStack", expectedStack);
    }

    private static async Task<long> ReadInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision
                ? revision
                : throw new InvalidDataException(
                    "The guild founder has no inventory revision.");
    }

    private static async Task AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        long expectedRevision,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
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
                "The guild founder's inventory revision did not advance.");
        }
    }

    private static async Task<long> PersistEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        string guildName,
        long inventoryRevision,
        long itemInstanceId,
        short slot,
        string beforeState,
        string? afterState,
        CancellationToken cancellationToken)
    {
        var operationId = Hash(
            $"{CreateCommandFamily}|{Guid.NewGuid():N}|{characterId}");
        var requestHash = Hash(
            $"{accountId}|{characterId}|{guildName}|{inventoryRevision}|" +
            $"{itemInstanceId}|{slot}|{beforeState}|{afterState}");
        const string resultCode = "created";
        var payload = JsonSerializer.Serialize(new
        {
            accountId,
            characterId,
            guildName,
            inventoryRevision,
            itemInstanceId,
            itemTemplateId = GuildStoneItemId,
            kitBagSlot = slot
        });
        var resultHash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        long auditId;
        await using (var audit = new NpgsqlCommand(
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
                        "The guild create audit returned no identity.");
        }

        await using var inbox = new NpgsqlCommand(
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
                    "The guild create inbox returned no identity.");
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
            accountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        command.Parameters.AddWithValue("commandFamily", CreateCommandFamily);
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

    private static async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        int accountId,
        int characterId,
        long inventoryRevision,
        long itemInstanceId,
        string beforeState,
        string? afterState,
        CancellationToken cancellationToken)
    {
        var mutationKind = afterState is null ? "delete" : "update";
        await using var command = new NpgsqlCommand(
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
            NpgsqlDbType.Jsonb).Value = beforeState;
        command.Parameters.Add(
            "afterState",
            NpgsqlDbType.Jsonb).Value = afterState is null
                ? DBNull.Value
                : afterState;
        command.Parameters.AddWithValue(
            "reasonCode",
            GuildStoneLedgerReason);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Guild Stone ledger append was not exact.");
        }
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
