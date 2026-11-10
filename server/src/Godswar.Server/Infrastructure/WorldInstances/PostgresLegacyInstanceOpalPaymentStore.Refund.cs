using Godswar.Server.Infrastructure.Warehouse;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceOpalPaymentStore
{
    private async Task<RefundMutation> RefundOpalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PendingPayment payment,
        CancellationToken cancellationToken)
    {
        var current = await LockCurrentRefundStackAsync(
            connection,
            transaction,
            payment,
            cancellationToken);
        if (current is not null)
        {
            var afterCompact =
                (CompactItemEntry.Parse(current.BeforeCompact) with
                {
                    Stack = checked((short)(current.Stack + 1))
                }).ToCompactString();
            await using var update = CreateCommand(
                """
                UPDATE public.character_items
                SET stack = stack + 1,
                    updated_at = now()
                WHERE id = @itemInstanceId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @slot
                  AND prop_id = @opalItemTemplateId
                  AND stack = @expectedStack
                RETURNING to_jsonb(character_items)::text;
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue(
                "itemInstanceId",
                current.ItemInstanceId);
            update.Parameters.AddWithValue(
                "characterId",
                payment.CharacterId);
            update.Parameters.AddWithValue("slot", current.Slot);
            update.Parameters.AddWithValue(
                "opalItemTemplateId",
                LegacyInstanceOpalPaymentPolicy.OpalItemTemplateId);
            update.Parameters.AddWithValue(
                "expectedStack",
                current.Stack);
            var afterState =
                await update.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidDataException(
                    "The Atlantis Opal refund stack was not incremented.");
            return new(
                current.ItemInstanceId,
                current.Slot,
                current.BeforeState,
                afterState,
                current.BeforeCompact,
                afterCompact);
        }

        var slot = await FindRefundSlotAsync(
            connection,
            transaction,
            payment.CharacterId,
            payment.OriginalSlot,
            cancellationToken);
        var restoredCompact =
            (CompactItemEntry.Parse(payment.BeforeCompact) with
            {
                Stack = 1
            }).ToCompactString();
        await using var insert = CreateCommand(
            """
            INSERT INTO public.character_items
            SELECT restored.*
            FROM jsonb_populate_record(
                NULL::public.character_items,
                    @beforeState || jsonb_build_object(
                    'id', nextval(pg_get_serial_sequence(
                        'public.character_items', 'id')),
                    'user_id', @characterId,
                    'item_location', 1,
                    'slot_index', @slot,
                    'stack', 1,
                    'created_at', now(),
                    'updated_at', now())) AS restored
            RETURNING id, to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        insert.Parameters.Add(
            "beforeState",
            NpgsqlDbType.Jsonb).Value = payment.BeforeState;
        insert.Parameters.AddWithValue(
            "characterId",
            payment.CharacterId);
        insert.Parameters.AddWithValue("slot", slot);
        await using var reader =
            await insert.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The consumed Atlantis Opal could not be restored.");
        }
        return new(
            reader.GetInt64(0),
            slot,
            BeforeState: null,
            reader.GetString(1),
            "[]",
            restoredCompact);
    }

    private async Task<CurrentRefundStack?> LockCurrentRefundStackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PendingPayment payment,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            SELECT
                item.id,
                item.slot_index,
                item.stack,
                to_jsonb(item)::text,
                {WarehouseItemStateCodec.SelectCompactColumns}
            FROM public.character_items item
            WHERE item.user_id = @characterId
              AND item.item_location = 1
              AND item.prop_id = @opalItemTemplateId
              AND item.stack BETWEEN 1 AND 98
              AND (
                    to_jsonb(item) - ARRAY[
                        'id', 'user_id', 'item_location', 'slot_index',
                        'stack', 'created_at', 'updated_at']::text[]
                  ) = (
                    @beforeState::jsonb - ARRAY[
                        'id', 'user_id', 'item_location', 'slot_index',
                        'stack', 'created_at', 'updated_at']::text[]
                  )
            ORDER BY
                CASE WHEN item.id = @itemInstanceId THEN 0 ELSE 1 END,
                item.slot_index,
                item.id
            LIMIT 1
            FOR UPDATE OF item;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            payment.ItemInstanceId);
        command.Parameters.AddWithValue(
            "characterId",
            payment.CharacterId);
        command.Parameters.AddWithValue(
            "opalItemTemplateId",
            LegacyInstanceOpalPaymentPolicy.OpalItemTemplateId);
        command.Parameters.Add(
            "beforeState",
            NpgsqlDbType.Jsonb).Value = payment.BeforeState;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.GetString(3),
                WarehouseItemStateCodec.ReadCompactItem(reader, 4).ToCompactString())
            : null;
    }

    private async Task<short> FindRefundSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short originalSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            WITH empty_slots AS (
                SELECT slot::smallint
                FROM generate_series(0, 95) AS slot
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM public.character_items item
                    WHERE item.user_id = @characterId
                      AND item.item_location = 1
                      AND item.slot_index = slot)
            )
            SELECT slot
            FROM empty_slots
            ORDER BY CASE WHEN slot = @originalSlot THEN 0 ELSE 1 END,
                     slot
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("originalSlot", originalSlot);
        return await command.ExecuteScalarAsync(cancellationToken)
            is short slot
                ? slot
                : throw new InvalidDataException(
                    "No kit-bag slot is available for the Atlantis Opal " +
                    "refund.");
    }

    private sealed record CurrentRefundStack(
        long ItemInstanceId,
        short Slot,
        short Stack,
        string BeforeState,
        string BeforeCompact);

    private sealed record RefundMutation(
        long ItemInstanceId,
        short Slot,
        string? BeforeState,
        string AfterState,
        string BeforeCompact,
        string AfterCompact);
}
