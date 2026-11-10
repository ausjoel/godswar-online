using Godswar.Server.Application.FactionCrier;
using Npgsql;

namespace Godswar.Server.Infrastructure.FactionCrier;

internal sealed partial class PostgresFactionCrierCommandExecutor
{
    private async Task<LockedKitBag> LockKitBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        var items = new List<LockedBagItem>();
        await using var command = CreateCommand(
            """
            SELECT
                id,
                slot_index,
                prop_id,
                attribute1, attribute2, attribute3, attribute4,
                attribute5,
                attribute_level1, attribute_level2,
                attribute_level3, attribute_level4,
                attribute_level5,
                item_quality, item_grade, bound, stack, item_exp,
                holy_suit_code, holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level,
                holy_socket4_effect_id, holy_socket4_level,
                holy_socket5_effect_id, holy_socket5_level,
                holy_socket6_effect_id, holy_socket6_level,
                to_jsonb(character_items)::text,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2,
                holy_socket1_value, holy_socket2_value,
                holy_socket3_value, holy_socket4_value
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index BETWEEN 0 AND 95
            ORDER BY slot_index, id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var occupied = new bool[96];
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(1);
            if (occupied[slot])
            {
                throw new InvalidDataException(
                    "The locked kit bag contains duplicate slots.");
            }
            occupied[slot] = true;
            var item = new LockedBagItem(
                reader.GetInt64(0),
                slot,
                reader.GetInt32(2),
                reader.GetInt16(15),
                reader.GetInt16(16),
                ReadCompactItem(reader).ToCompactString(),
                reader.GetString(32));
            var isNameplate = item.ItemId is >=
                FactionCrierRewardPolicy.FirstNameplateItemId and <=
                FactionCrierRewardPolicy.LastNameplateItemId;
            if (item.Stack < 1 ||
                isNameplate &&
                    item.Stack > FactionCrierRewardPolicy.MaximumStack ||
                item.Bound is < 0 or > 1)
            {
                throw new InvalidDataException(
                    "The locked kit-bag row is invalid.");
            }
            items.Add(item);
        }
        return new LockedKitBag(items, occupied);
    }

    private static InventoryMutationPlan PlanInventoryMutation(
        FactionCrierCommand command,
        FactionCrierExecutionPlan plan,
        LockedKitBag bag)
    {
        var changes = new Dictionary<long, PlannedBagMutation>();
        var working = bag.Items.ToDictionary(
            static item => item.Id,
            static item => item.Stack);

        foreach (var itemId in plan.ConsumedItemIds)
        {
            LockedBagItem? candidate;
            if (plan.Operation == FactionCrierOperation.RenewNameplate &&
                command.RenewalSource is { } selected)
            {
                candidate = bag.Items.SingleOrDefault(item =>
                    item.Slot == selected.KitBagSlot &&
                    item.ItemId == selected.ItemId &&
                    item.ItemId == itemId &&
                    item.Bound == 1 &&
                    string.Equals(
                        item.CompactState,
                        selected.ExpectedCompactItemState,
                        StringComparison.Ordinal) &&
                    working[item.Id] > 0);
            }
            else
            {
                candidate = bag.Items.FirstOrDefault(item =>
                    item.ItemId == itemId &&
                    item.Bound == 1 &&
                    working[item.Id] > 0);
            }
            if (candidate is null)
            {
                return InventoryMutationPlan.Rejected(
                    FactionCrierExecutionDisposition.MissingNameplate);
            }

            var after = checked((short)(working[candidate.Id] - 1));
            working[candidate.Id] = after;
            changes[candidate.Id] = new PlannedBagMutation(
                candidate,
                after);
        }

        PlannedBagInsert? insert = null;
        if (plan.GrantedItemId is { } grantedItemId)
        {
            var fillable = bag.Items.FirstOrDefault(item =>
                item.ItemId == grantedItemId &&
                item.Bound == 1 &&
                working[item.Id] is > 0 and <
                    FactionCrierRewardPolicy.MaximumStack);
            if (fillable is not null)
            {
                var after = checked((short)(working[fillable.Id] + 1));
                working[fillable.Id] = after;
                changes[fillable.Id] = new PlannedBagMutation(
                    fillable,
                    after);
            }
            else
            {
                var occupiedAfter = bag.Items
                    .Where(item => working[item.Id] > 0)
                    .Select(static item => (int)item.Slot)
                    .ToHashSet();
                var emptySlot = Enumerable.Range(0, 96)
                    .FirstOrDefault(
                        slot => !occupiedAfter.Contains(slot),
                        -1);
                if (emptySlot < 0)
                {
                    return InventoryMutationPlan.Rejected(
                        FactionCrierExecutionDisposition.BagFull);
                }
                insert = new PlannedBagInsert(
                    checked((short)emptySlot),
                    grantedItemId);
            }
        }

        if (changes.Count == 0 && insert is null)
        {
            throw new InvalidDataException(
                "The Faction Crier operation planned no inventory mutation.");
        }
        return InventoryMutationPlan.Accepted(
            changes.Values.OrderBy(static value => value.Item.Slot).ToArray(),
            insert);
    }

    private async Task<IReadOnlyList<AppliedInventoryMutation>>
        ApplyInventoryMutationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            InventoryMutationPlan plan,
            CancellationToken cancellationToken)
    {
        var applied = new List<AppliedInventoryMutation>(
            plan.Changes.Count + (plan.Insert is null ? 0 : 1));
        foreach (var mutation in plan.Changes)
        {
            if (mutation.StackAfter == 0)
            {
                await using var command = CreateCommand(
                    """
                    DELETE FROM public.character_items
                    WHERE id = @itemId
                      AND stack = @stackBefore;
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue("itemId", mutation.Item.Id);
                command.Parameters.AddWithValue(
                    "stackBefore",
                    mutation.Item.Stack);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidDataException(
                        "A consumed Nameplate was not deleted exactly once.");
                }
                applied.Add(new(
                    mutation.Item.Id,
                    "delete",
                    mutation.Item.BeforeState,
                    null));
                continue;
            }

            await using (var command = CreateCommand(
                """
                UPDATE public.character_items
                SET stack = @stackAfter,
                    updated_at = now()
                WHERE id = @itemId
                  AND stack = @stackBefore
                RETURNING to_jsonb(character_items)::text;
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue(
                    "stackAfter",
                    mutation.StackAfter);
                command.Parameters.AddWithValue("itemId", mutation.Item.Id);
                command.Parameters.AddWithValue(
                    "stackBefore",
                    mutation.Item.Stack);
                var after = await command.ExecuteScalarAsync(cancellationToken)
                    as string ?? throw new InvalidDataException(
                        "A Nameplate stack was not updated exactly once.");
                applied.Add(new(
                    mutation.Item.Id,
                    "update",
                    mutation.Item.BeforeState,
                    after));
            }
        }

        if (plan.Insert is { } insert)
        {
            await using var command = CreateCommand(
                """
                INSERT INTO public.character_items (
                    user_id,
                    item_location,
                    slot_index,
                    prop_id,
                    item_quality,
                    item_grade,
                    bound,
                    stack,
                    item_exp,
                    holy_suit_code
                )
                VALUES (
                    @characterId,
                    1,
                    @slot,
                    @itemId,
                    1,
                    1,
                    1,
                    1,
                    0,
                    0
                )
                RETURNING id, to_jsonb(character_items)::text;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("slot", insert.Slot);
            command.Parameters.AddWithValue("itemId", insert.ItemId);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "The granted Nameplate insert returned no row.");
            }
            applied.Add(new(
                reader.GetInt64(0),
                "add",
                null,
                reader.GetString(1)));
        }
        return applied;
    }

    private sealed record LockedKitBag(
        IReadOnlyList<LockedBagItem> Items,
        bool[] Occupied);

    private sealed record LockedBagItem(
        long Id,
        short Slot,
        int ItemId,
        short Bound,
        short Stack,
        string CompactState,
        string BeforeState);

    private sealed record PlannedBagMutation(
        LockedBagItem Item,
        short StackAfter);

    private sealed record PlannedBagInsert(short Slot, int ItemId);

    private sealed record InventoryMutationPlan(
        FactionCrierExecutionDisposition Disposition,
        IReadOnlyList<PlannedBagMutation> Changes,
        PlannedBagInsert? Insert)
    {
        public static InventoryMutationPlan Accepted(
            IReadOnlyList<PlannedBagMutation> changes,
            PlannedBagInsert? insert) =>
            new(FactionCrierExecutionDisposition.Committed, changes, insert);

        public static InventoryMutationPlan Rejected(
            FactionCrierExecutionDisposition disposition) =>
            new(disposition, [], null);
    }

    private sealed record AppliedInventoryMutation(
        long ItemInstanceId,
        string Kind,
        string? BeforeState,
        string? AfterState);
}
