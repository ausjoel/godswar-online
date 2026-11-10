using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandChestClaimStore
{
    private static async Task<Dictionary<short, ChestBagItem>> ReadBagAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT item.item_location,item.slot_index,item.prop_id,
                item.attribute1,item.attribute2,item.attribute3,item.attribute4,item.attribute5,
                item.attribute_level1,item.attribute_level2,item.attribute_level3,item.attribute_level4,item.attribute_level5,
                item.item_quality,item.item_grade,item.bound,item.stack,item.item_exp,item.holy_suit_code,
                item.holy_socket_count,item.holy_socket1_effect_id,item.holy_socket1_level,
                item.holy_socket2_effect_id,item.holy_socket2_level,item.holy_socket3_effect_id,item.holy_socket3_level,
                item.holy_socket4_effect_id,item.holy_socket4_level,item.holy_socket5_effect_id,item.holy_socket5_level,
                item.holy_socket6_effect_id,item.holy_socket6_level,item.class_attribute1,item.class_attribute2,
                item.elemental_attribute1,item.elemental_attribute2,item.holy_socket1_value,item.holy_socket2_value,
                item.holy_socket3_value,item.holy_socket4_value,sealed_link.pet_id,item.id,to_jsonb(item)::text
            FROM character_items item LEFT JOIN sealed_pet_items sealed_link
              ON sealed_link.item_instance_id=item.id AND item.prop_id=10109 AND sealed_link.owner_character_id=item.user_id
            WHERE item.user_id=@character AND item.item_location=1 AND item.slot_index BETWEEN 0 AND 95
            ORDER BY item.slot_index FOR UPDATE OF item;
            """, connection, transaction);
        command.Parameters.AddWithValue("character", characterId);
        var result = new Dictionary<short, ChestBagItem>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(reader.GetInt16(1), new(reader.GetInt64(41),
                PostgresCompactItemReader.ReadAuthoritativeCompactItem(reader), reader.GetString(42)));
        return result;
    }

    private static Dictionary<short, CompactItemEntry>? PlanRewards(Dictionary<short, ChestBagItem> bag,
        IReadOnlyList<WonderlandChestItemReward> rewards)
    {
        var kitBag = string.Empty;
        foreach (var entry in bag)
            kitBag = KitBagSlots.SetSlot(kitBag, entry.Key, entry.Value.Item.ToCompactString());
        foreach (var reward in rewards)
            if (!KitBagItemGrantPlanner.TryAdd(kitBag, reward.ItemId, reward.Quantity,
                    WonderlandChestRewardPolicy.StackCap, reward.Bound, out kitBag)) return null;
        var result = new Dictionary<short, CompactItemEntry>();
        for (short slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            var item = KitBagSlots.GetItem(kitBag, slot);
            if (item.IsEmpty) continue;
            if (!bag.TryGetValue(slot, out var before)) result.Add(slot, item);
            else if (item.Stack != before.Item.Stack)
            {
                if (item.Id != before.Item.Id || item.Bound != before.Item.Bound || item.Stack < before.Item.Stack)
                    throw new InvalidDataException("Chest stack planner changed item identity.");
                // Preserve authoritative attributes even if a legacy compact
                // representation would normalize their ordering on parsing.
                result.Add(slot, before.Item with { Stack = item.Stack });
            }
        }
        return result;
    }

    private static async Task<List<ChestInventoryMutation>> ApplyRewardsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, Dictionary<short, ChestBagItem> bag,
        Dictionary<short, CompactItemEntry> plan, CancellationToken token)
    {
        var result = new List<ChestInventoryMutation>();
        foreach (var (slot, after) in plan.OrderBy(entry => entry.Key))
        {
            var existing = bag.GetValueOrDefault(slot);
            if (existing is null)
                await PostgresCompactItemWriter.InsertCharacterItemIntoEmptySlotAsync(connection, transaction,
                    characterId, 1, slot, after, token);
            else
            {
                if ((after with { Stack = existing.Item.Stack }) != existing.Item || after.Stack <= existing.Item.Stack)
                    throw new InvalidDataException("Chest grant attempted a non-stack change to an owned item.");
                await using var update = new NpgsqlCommand("""
                    UPDATE character_items SET stack=@stack,updated_at=now() WHERE id=@id AND stack=@before;
                    """, connection, transaction);
                update.Parameters.AddWithValue("stack", after.Stack);
                update.Parameters.AddWithValue("id", existing.Id);
                update.Parameters.AddWithValue("before", existing.Item.Stack);
                if (await update.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidDataException("Chest grant lost a locked stack.");
            }
            await using var read = new NpgsqlCommand("""
                SELECT id,to_jsonb(character_items)::text FROM character_items
                WHERE user_id=@character AND item_location=1 AND slot_index=@slot;
                """, connection, transaction);
            read.Parameters.AddWithValue("character", characterId);
            read.Parameters.AddWithValue("slot", slot);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new InvalidDataException("Chest grant item was not persisted.");
            result.Add(new(reader.GetInt64(0), existing is null ? "add" : "update", existing?.Json, reader.GetString(1)));
        }
        return result;
    }

    private sealed record ChestBagItem(long Id, CompactItemEntry Item, string Json);
    private sealed record ChestInventoryMutation(long Id, string Kind, string? Before, string After);
}
