using static Godswar.Server.Infrastructure.Inventory.PostgresCompactItemReader;
using static Godswar.Server.Infrastructure.Inventory.PostgresCompactItemWriter;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresCapitalShopPurchaseStore
{
    private static async Task<CapitalShopBag> LockCapitalShopBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CompactItemEntry offeredItem,
        short stackCap,
        CancellationToken cancellationToken)
    {
        var occupied = new bool[KitBagProjectionSlots];
        var stacks = new List<CapitalShopStack>();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                item.item_location, item.slot_index, item.prop_id,
                item.attribute1, item.attribute2, item.attribute3,
                item.attribute4, item.attribute5,
                item.attribute_level1, item.attribute_level2,
                item.attribute_level3, item.attribute_level4,
                item.attribute_level5,
                item.item_quality, item.item_grade, item.bound,
                item.stack, item.item_exp, item.holy_suit_code,
                item.holy_socket_count,
                item.holy_socket1_effect_id, item.holy_socket1_level,
                item.holy_socket2_effect_id, item.holy_socket2_level,
                item.holy_socket3_effect_id, item.holy_socket3_level,
                item.holy_socket4_effect_id, item.holy_socket4_level,
                item.holy_socket5_effect_id, item.holy_socket5_level,
                item.holy_socket6_effect_id, item.holy_socket6_level,
                item.class_attribute1, item.class_attribute2,
                item.elemental_attribute1, item.elemental_attribute2,
                item.holy_socket1_value, item.holy_socket2_value,
                item.holy_socket3_value, item.holy_socket4_value,
                sealed_link.pet_id,
                item.id, to_jsonb(item)::text
            FROM public.character_items item
            LEFT JOIN public.sealed_pet_items sealed_link
              ON sealed_link.item_instance_id = item.id
             AND item.prop_id = 10109
             AND sealed_link.owner_character_id = item.user_id
            WHERE item.user_id = @characterId
              AND item.item_location = 1
              AND item.slot_index BETWEEN 0 AND 95
            ORDER BY item.slot_index, item.id
            FOR UPDATE OF item;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(1);
            if (occupied[slot])
            {
                throw new InvalidDataException(
                    "The locked kit bag contains duplicate slots.");
            }

            occupied[slot] = true;
            var existingItem = ReadAuthoritativeCompactItem(reader);
            if (!IsCapitalShopStackCompatible(existingItem, offeredItem))
            {
                continue;
            }
            if (existingItem.Stack > stackCap)
            {
                throw new InvalidDataException(
                    "A shop target stack exceeds its item-template cap.");
            }
            if (existingItem.Stack < stackCap)
            {
                stacks.Add(new CapitalShopStack(
                    reader.GetInt64(41),
                    slot,
                    existingItem.Stack,
                    reader.GetString(42)));
            }
        }

        return new CapitalShopBag(stacks, occupied);
    }

    internal static bool IsCapitalShopStackCompatible(
        CompactItemEntry existingItem,
        CompactItemEntry offeredItem) =>
        !existingItem.IsEmpty &&
        !offeredItem.IsEmpty &&
        existingItem with { Stack = offeredItem.Stack } == offeredItem;

    internal static CapitalShopPlan? PlanCapitalShopMutation(
        CapitalShopBag bag,
        int quantity,
        short stackCap)
    {
        ArgumentNullException.ThrowIfNull(bag);
        if (bag.Occupied.Length != KitBagProjectionSlots ||
            quantity is < 1 or > byte.MaxValue ||
            stackCap is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        var remaining = quantity;
        var updates = new List<CapitalShopUpdate>();
        foreach (var stack in bag.Stacks
                     .OrderBy(static value => value.Slot)
                     .ThenBy(static value => value.InstanceId))
        {
            if (stack.Stack is < 1 || stack.Stack >= stackCap)
            {
                throw new InvalidDataException(
                    "The shop merge plan contains an invalid partial stack.");
            }

            var added = Math.Min(remaining, stackCap - stack.Stack);
            updates.Add(new CapitalShopUpdate(
                stack,
                checked((short)(stack.Stack + added))));
            remaining -= added;
            if (remaining == 0)
            {
                break;
            }
        }

        var inserts = new List<CapitalShopInsert>();
        for (short slot = 0;
             slot < bag.Occupied.Length && remaining > 0;
             slot++)
        {
            if (bag.Occupied[slot])
            {
                continue;
            }

            var stack = checked((short)Math.Min(remaining, stackCap));
            inserts.Add(new CapitalShopInsert(slot, stack));
            remaining -= stack;
        }

        return remaining == 0
            ? new CapitalShopPlan(updates, inserts)
            : null;
    }

    private static async Task<IReadOnlyList<CapitalShopMutation>>
        ApplyCapitalShopItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CompactItemEntry offeredItem,
        CapitalShopPlan plan,
        CancellationToken cancellationToken)
    {
        var mutations = new List<CapitalShopMutation>(
            plan.Updates.Count + plan.Inserts.Count);
        foreach (var update in plan.Updates)
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE public.character_items
                SET stack = @stackAfter,
                    updated_at = transaction_timestamp()
                WHERE id = @itemInstanceId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @slot
                  AND stack = @stackBefore
                RETURNING to_jsonb(character_items)::text;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(
                "stackAfter",
                update.StackAfter);
            command.Parameters.AddWithValue(
                "itemInstanceId",
                update.Stack.InstanceId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("slot", update.Stack.Slot);
            command.Parameters.AddWithValue(
                "stackBefore",
                update.Stack.Stack);
            var afterState =
                await command.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidDataException(
                    "A purchased item stack was not updated exactly once.");
            mutations.Add(new CapitalShopMutation(
                update.Stack.InstanceId,
                update.Stack.Slot,
                "update",
                update.Stack.BeforeState,
                afterState));
        }

        foreach (var insert in plan.Inserts)
        {
            await InsertCharacterItemIntoEmptySlotAsync(
                connection,
                transaction,
                characterId,
                ItemLocationKitBag,
                insert.Slot,
                offeredItem with { Stack = insert.Stack },
                cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                SELECT id, to_jsonb(character_items)::text
                FROM public.character_items
                WHERE user_id = @characterId AND item_location = 1
                  AND slot_index = @slot
                FOR UPDATE;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("slot", insert.Slot);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "A purchased item insert returned no durable row.");
            }
            mutations.Add(new CapitalShopMutation(
                reader.GetInt64(0),
                insert.Slot,
                "add",
                null,
                reader.GetString(1)));
        }

        return mutations;
    }

    internal sealed record CapitalShopBag(
        IReadOnlyList<CapitalShopStack> Stacks,
        bool[] Occupied);

    internal sealed record CapitalShopStack(
        long InstanceId,
        short Slot,
        short Stack,
        string BeforeState);

    internal sealed record CapitalShopUpdate(
        CapitalShopStack Stack,
        short StackAfter);

    internal readonly record struct CapitalShopInsert(
        short Slot,
        short Stack);

    internal sealed record CapitalShopPlan(
        IReadOnlyList<CapitalShopUpdate> Updates,
        IReadOnlyList<CapitalShopInsert> Inserts);

    private readonly record struct CapitalShopMutation(
        long ItemInstanceId,
        short Slot,
        string Kind,
        string? BeforeState,
        string AfterState);
}
