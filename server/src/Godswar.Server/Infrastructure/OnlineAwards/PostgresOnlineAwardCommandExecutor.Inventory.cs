using Godswar.Server.Application.Commands;
using Godswar.Server.Application.OnlineAwards;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.OnlineAwards;

internal sealed partial class PostgresOnlineAwardCommandExecutor
{
    private async Task<LockedKitBag> LockKitBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        var occupied = new bool[KitBagSlots];
        var items = new List<LockedBagItem>();
        await using var command = CreateCommand(
            """
            SELECT id, slot_index, prop_id, item_quality,
                   bound, stack, to_jsonb(character_items)::text
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
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(1);
            if (occupied[slot])
            {
                throw new InvalidDataException(
                    "The Online Award kit bag has a duplicate slot.");
            }
            occupied[slot] = true;
            var item = new LockedBagItem(
                reader.GetInt64(0),
                slot,
                reader.GetInt32(2),
                reader.GetInt16(3),
                reader.GetInt16(4),
                reader.GetInt16(5),
                reader.GetString(6));
            if (item.Stack <= 0 || item.Bound is < 0 or > 1)
            {
                throw new InvalidDataException(
                    "The Online Award kit-bag row is invalid.");
            }
            items.Add(item);
        }
        return new LockedKitBag(items, occupied);
    }

    private static InventoryPlan? PlanInventory(
        LockedKitBag bag,
        IReadOnlyList<OnlineAwardRewardEntry> rewards)
    {
        foreach (var reward in rewards)
        {
            if (bag.Items.Any(item =>
                    item.ItemId == reward.ItemId &&
                    item.ItemQuality == reward.ItemQuality &&
                    item.Bound == reward.Bound &&
                    item.Stack > reward.StackCap))
            {
                throw new InvalidDataException(
                    "An Online Award target stack exceeds pinned capacity.");
            }
        }

        var working = bag.Items.ToDictionary(
            static item => item.InstanceId,
            static item => item.Stack);
        var additions = new Dictionary<long, short>();
        var inserts = new List<PlannedInsert>();
        var emptySlots = new Queue<short>(
            Enumerable.Range(0, KitBagSlots)
                .Where(slot => !bag.Occupied[slot])
                .Select(static slot => checked((short)slot)));

        foreach (var reward in rewards.OrderBy(static value => value.Order))
        {
            var remaining = reward.Quantity;
            foreach (var item in bag.Items.Where(item =>
                         item.ItemId == reward.ItemId &&
                         item.ItemQuality == reward.ItemQuality &&
                         item.Bound == reward.Bound &&
                         working[item.InstanceId] < reward.StackCap))
            {
                if (remaining == 0)
                {
                    break;
                }
                var added = checked((short)Math.Min(
                    remaining,
                    reward.StackCap - working[item.InstanceId]));
                working[item.InstanceId] = checked(
                    (short)(working[item.InstanceId] + added));
                additions[item.InstanceId] = checked((short)(
                    additions.GetValueOrDefault(item.InstanceId) + added));
                remaining -= added;
            }

            while (remaining > 0)
            {
                if (emptySlots.Count == 0)
                {
                    return null;
                }
                var stack = checked((short)Math.Min(
                    remaining,
                    reward.StackCap));
                inserts.Add(new PlannedInsert(
                    emptySlots.Dequeue(),
                    reward.ItemId,
                    reward.ItemQuality,
                    reward.Bound,
                    stack));
                remaining -= stack;
            }
        }

        var updates = additions.Select(pair =>
        {
            var item = bag.Items.Single(value =>
                value.InstanceId == pair.Key);
            return new PlannedUpdate(
                item,
                checked((short)(item.Stack + pair.Value)));
        }).OrderBy(static value => value.Item.Slot).ToArray();
        return new InventoryPlan(updates, inserts);
    }

    private async Task<IReadOnlyList<InventoryMutation>> ApplyInventoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        InventoryPlan plan,
        CancellationToken cancellationToken)
    {
        var mutations = new List<InventoryMutation>(
            plan.Updates.Count + plan.Inserts.Count);
        foreach (var update in plan.Updates)
        {
            await using var command = CreateCommand(
                """
                UPDATE public.character_items
                SET stack = @stackAfter, updated_at = now()
                WHERE id = @instanceId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND stack = @stackBefore
                RETURNING to_jsonb(character_items)::text;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("stackAfter", update.StackAfter);
            command.Parameters.AddWithValue(
                "instanceId",
                update.Item.InstanceId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue(
                "stackBefore",
                update.Item.Stack);
            var after = await command.ExecuteScalarAsync(cancellationToken)
                as string ?? throw new InvalidDataException(
                    "An Online Award stack update was not exact.");
            mutations.Add(new(
                update.Item.InstanceId,
                "update",
                update.Item.BeforeState,
                after));
        }

        foreach (var insert in plan.Inserts)
        {
            await using var command = CreateCommand(
                """
                INSERT INTO public.character_items (
                    user_id, item_location, slot_index, prop_id,
                    item_quality, item_grade, bound, stack,
                    item_exp, holy_suit_code)
                VALUES (@characterId, 1, @slot, @itemId,
                        @quality, 1, @bound, @stack, 0, 0)
                RETURNING id, to_jsonb(character_items)::text;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("slot", insert.Slot);
            command.Parameters.AddWithValue("itemId", insert.ItemId);
            command.Parameters.AddWithValue("quality", insert.ItemQuality);
            command.Parameters.AddWithValue("bound", insert.Bound);
            command.Parameters.AddWithValue("stack", insert.Stack);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "An Online Award item insert returned no state.");
            }
            mutations.Add(new(
                reader.GetInt64(0),
                "add",
                null,
                reader.GetString(1)));
        }

        return mutations;
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CommandEnvelope<OnlineAwardCommand> envelope,
        long inventoryRevision,
        IReadOnlyList<InventoryMutation> mutations,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id, account_id, character_id,
                inventory_revision, entry_ordinal, item_instance_id,
                mutation_kind, state_contract_version,
                before_state, after_state, reason_code)
            VALUES (@inboxId, @accountId, @characterId,
                    @inventoryRevision, @ordinal, @instanceId,
                    @kind, 1, @beforeState, @afterState,
                    'online_award_daily_claim');
            """,
            connection,
            transaction);
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            command.Parameters.Clear();
            command.Parameters.AddWithValue("inboxId", inboxId);
            command.Parameters.AddWithValue(
                "accountId",
                envelope.Subject.AccountId);
            command.Parameters.AddWithValue(
                "characterId",
                envelope.Subject.CharacterId);
            command.Parameters.AddWithValue(
                "inventoryRevision",
                inventoryRevision);
            command.Parameters.AddWithValue("ordinal", checked((short)index));
            command.Parameters.AddWithValue(
                "instanceId",
                mutation.InstanceId);
            command.Parameters.AddWithValue("kind", mutation.Kind);
            AddJson(command, "beforeState", mutation.BeforeState);
            AddJson(command, "afterState", mutation.AfterState);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "An Online Award inventory ledger row was not exact.");
            }
        }
    }

    private static void AddJson(
        NpgsqlCommand command,
        string name,
        string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value =
            value is null ? DBNull.Value : value;

    private sealed record LockedKitBag(
        IReadOnlyList<LockedBagItem> Items,
        bool[] Occupied);

    private sealed record LockedBagItem(
        long InstanceId,
        short Slot,
        int ItemId,
        short ItemQuality,
        short Bound,
        short Stack,
        string BeforeState);

    private sealed record PlannedUpdate(
        LockedBagItem Item,
        short StackAfter);

    private sealed record PlannedInsert(
        short Slot,
        int ItemId,
        short ItemQuality,
        short Bound,
        short Stack);

    private sealed record InventoryPlan(
        IReadOnlyList<PlannedUpdate> Updates,
        IReadOnlyList<PlannedInsert> Inserts);

    private sealed record InventoryMutation(
        long InstanceId,
        string Kind,
        string? BeforeState,
        string? AfterState);
}
