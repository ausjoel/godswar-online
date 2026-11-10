using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<Dictionary<short, WonderlandSackBagRow>> ReadWonderlandSackBagAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int characterId, CancellationToken token)
    {
        await using var command = CreateCommand("""
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
        var rows = new Dictionary<short, WonderlandSackBagRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(reader.GetInt16(1), new(reader.GetInt64(41),
                PostgresCompactItemReader.ReadAuthoritativeCompactItem(reader), reader.GetString(42)));
        return rows;
    }

    private async Task<List<InventoryMutation>> GrantWonderlandSackRewardAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, Dictionary<short, WonderlandSackBagRow> before,
        string planned, uint rewardId, int sackSlot, bool sackDeleted, long revision, CancellationToken token,
        string reasonCode = "wonderland_sack_reward")
    {
        var result = new List<InventoryMutation>();
        for (short slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            var after = KitBagSlots.GetItem(planned, slot);
            if (after.Id != rewardId) continue;
            var existing = sackDeleted && slot == sackSlot ? null : before.GetValueOrDefault(slot);
            if (existing is not null && existing.Item.Stack == after.Stack) continue;
            if (existing is null)
                await PostgresCompactItemWriter.InsertCharacterItemIntoEmptySlotAsync(connection, transaction,
                    characterId, 1, slot, after, token);
            else
            {
                if (existing.Item.Id != after.Id || existing.Item.Bound != after.Bound || existing.Item.Stack >= after.Stack)
                    throw new InvalidDataException("Wonderland reward planning changed an owned item's identity.");
                // Only the authoritative stack changes; every owned attribute remains intact.
                await using var update = CreateCommand("""
                    UPDATE character_items SET stack=@stack,updated_at=transaction_timestamp()
                    WHERE id=@id AND user_id=@character AND stack=@before;
                    """, connection, transaction);
                update.Parameters.AddWithValue("stack", after.Stack);
                update.Parameters.AddWithValue("id", existing.Id);
                update.Parameters.AddWithValue("character", characterId);
                update.Parameters.AddWithValue("before", existing.Item.Stack);
                if (await update.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidDataException("Wonderland reward grant lost its locked stack.");
            }
            await using var read = CreateCommand("""
                SELECT id,to_jsonb(character_items)::text FROM character_items
                WHERE user_id=@character AND item_location=1 AND slot_index=@slot;
                """, connection, transaction);
            read.Parameters.AddWithValue("character", characterId);
            read.Parameters.AddWithValue("slot", slot);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new InvalidDataException("Wonderland sack reward was not persisted.");
            result.Add(new(reader.GetInt64(0), existing is null ? "add" : "update", existing?.Json,
                reader.GetString(1), reasonCode, revision));
        }
        if (result.Count == 0) throw new InvalidDataException("Wonderland sack opening has no reward mutation.");
        return result;
    }

    private sealed record WonderlandSackBagRow(long Id, CompactItemEntry Item, string Json);
}
