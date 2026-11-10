using Godswar.Server.Infrastructure.Warehouse;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceOpalPaymentStore
{
    private async Task RequirePendingDailyEntriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LegacyInstanceOpalChargeRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT character_id
            FROM public.legacy_instance_daily_entries
            WHERE reservation_id = @reservationId
              AND realm_id = @realmId
              AND instance_kind = 1
              AND character_id = ANY(@characterIds)
              AND admitted_at IS NULL
            ORDER BY character_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "reservationId",
            request.ReservationId);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)request.RealmId.Value));
        command.Parameters.Add(
            "characterIds",
            NpgsqlTypes.NpgsqlDbType.Array |
            NpgsqlTypes.NpgsqlDbType.Integer).Value =
            request.Payers
                .Select(static payer => payer.CharacterId)
                .ToArray();
        var stored = new List<int>(request.Payers.Count);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stored.Add(reader.GetInt32(0));
        }
        var expected = request.Payers
            .Select(static payer => payer.CharacterId)
            .Order()
            .ToArray();
        if (!stored.SequenceEqual(expected))
        {
            throw new InvalidDataException(
                "Atlantis Opal payers do not own matching pending daily " +
                "entry reservations.");
        }
    }

    private async Task<long> ReadInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LegacyInstanceOpalPayer payer,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            "SELECT inventory_revision FROM public.character_base " +
            "WHERE account_id = @accountId AND id = @characterId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", payer.AccountId);
        command.Parameters.AddWithValue("characterId", payer.CharacterId);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision >= 0
                ? revision
                : throw new InvalidDataException(
                    "The locked Atlantis Opal payer disappeared.");
    }

    private async Task<LockedOpal?> LockOpalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
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
              AND item.stack > 0
            ORDER BY item.slot_index, item.id
            LIMIT 1
            FOR UPDATE OF item;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "opalItemTemplateId",
            LegacyInstanceOpalPaymentPolicy.OpalItemTemplateId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var stack = reader.GetInt16(2);
        var beforeCompact = WarehouseItemStateCodec.ReadCompactItem(reader, 4).ToCompactString();
        var afterCompact = stack == 1
            ? "[]"
            : (CompactItemEntry.Parse(beforeCompact) with
                {
                    Stack = checked((short)(stack - 1))
                }).ToCompactString();
        return new(
            reader.GetInt64(0),
            reader.GetInt16(1),
            stack,
            reader.GetString(3),
            beforeCompact,
            afterCompact);
    }

    private async Task<InventoryMutation> ConsumeOpalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LegacyInstanceOpalPayer payer,
        LockedOpal item,
        CancellationToken cancellationToken)
    {
        if (item.Stack > 1)
        {
            await using var update = CreateCommand(
                """
                UPDATE public.character_items
                SET stack = stack - 1,
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
            AddOpalIdentityParameters(update, payer.CharacterId, item);
            update.Parameters.AddWithValue("expectedStack", item.Stack);
            var afterState =
                await update.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidDataException(
                    "The locked Atlantis Opal stack was not decremented.");
            return new(item.BeforeState, afterState);
        }

        await using var delete = CreateCommand(
            """
            WITH deleted AS (
                DELETE FROM public.character_items
                WHERE id = @itemInstanceId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @slot
                  AND prop_id = @opalItemTemplateId
                  AND stack = 1
                RETURNING *
            )
            INSERT INTO public.character_item_audit (
                source, action, user_id, item_location, slot_index,
                prop_id, item_quality, item_grade, item_exp, old_item)
            SELECT
                'legacy-instance-opal', 'delete', user_id, item_location,
                slot_index, prop_id, item_quality, item_grade, item_exp,
                to_jsonb(deleted)
            FROM deleted;
            """,
            connection,
            transaction);
        AddOpalIdentityParameters(delete, payer.CharacterId, item);
        if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The locked Atlantis Opal was not deleted.");
        }
        return new(item.BeforeState, AfterState: null);
    }

    private static void AddOpalIdentityParameters(
        NpgsqlCommand command,
        int characterId,
        LockedOpal item)
    {
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            item.ItemInstanceId);
        command.Parameters.AddWithValue("slot", item.Slot);
        command.Parameters.AddWithValue(
            "opalItemTemplateId",
            LegacyInstanceOpalPaymentPolicy.OpalItemTemplateId);
    }
}
