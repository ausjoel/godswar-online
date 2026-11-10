using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class PostgresCompactItemWriter
{
    internal static async Task InsertCharacterItemIntoEmptySlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short itemLocation,
        int slotIndex,
        CompactItemEntry item,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                attribute1, attribute2, attribute3, attribute4, attribute5,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2,
                attribute_level1, attribute_level2, attribute_level3, attribute_level4, attribute_level5,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code,
                holy_socket_count, holy_socket1_effect_id, holy_socket1_level, holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level, holy_socket4_effect_id, holy_socket4_level,
                holy_socket5_effect_id, holy_socket5_level, holy_socket6_effect_id, holy_socket6_level,
                holy_socket1_value, holy_socket2_value, holy_socket3_value, holy_socket4_value
            )
            VALUES (
                @characterId, @itemLocation, @slotIndex, @itemId,
                @attribute1, @attribute2, @attribute3, @attribute4, @attribute5,
                @classAttribute1, @classAttribute2,
                @elementalAttribute1, @elementalAttribute2,
                @attributeLevel1, @attributeLevel2, @attributeLevel3, @attributeLevel4, @attributeLevel5,
                @itemQuality, @itemGrade, @bound, @stack, @itemExp, @holySuitCode,
                @holySocketCount, @holySocket1EffectId, @holySocket1Level, @holySocket2EffectId, @holySocket2Level,
                @holySocket3EffectId, @holySocket3Level, @holySocket4EffectId, @holySocket4Level,
                @holySocket5EffectId, @holySocket5Level, @holySocket6EffectId, @holySocket6Level,
                @holySocket1Value, @holySocket2Value, @holySocket3Value, @holySocket4Value
            )
            ON CONFLICT (user_id, item_location, slot_index) DO NOTHING;
            """, connection, transaction);
        AddCharacterItemParameters(command, characterId, itemLocation, slotIndex, item);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Holy-stone destination changed at location {itemLocation}, slot {slotIndex}.");
        }
    }

    internal static void AddCharacterItemParameters(
        NpgsqlCommand command,
        int characterId,
        short itemLocation,
        int slotIndex,
        CompactItemEntry item)
    {
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemLocation", itemLocation);
        command.Parameters.AddWithValue("slotIndex", (short)slotIndex);
        command.Parameters.AddWithValue("itemId", (int)item.Id);
        AddAttributeParameter(command, "attribute1", item.Attribute1);
        AddAttributeParameter(command, "attribute2", item.Attribute2);
        AddAttributeParameter(command, "attribute3", item.Attribute3);
        AddAttributeParameter(command, "attribute4", item.Attribute4);
        AddAttributeParameter(command, "attribute5", item.Attribute5);
        AddAttributeParameter(command, "classAttribute1", item.ClassAttribute1);
        AddAttributeParameter(command, "classAttribute2", item.ClassAttribute2);
        AddAttributeParameter(command, "elementalAttribute1", item.ElementalAttribute1);
        AddAttributeParameter(command, "elementalAttribute2", item.ElementalAttribute2);
        AddNullableSmallintParameter(command, "attributeLevel1", item.AttributeLevel1);
        AddNullableSmallintParameter(command, "attributeLevel2", item.AttributeLevel2);
        AddNullableSmallintParameter(command, "attributeLevel3", item.AttributeLevel3);
        AddNullableSmallintParameter(command, "attributeLevel4", item.AttributeLevel4);
        AddNullableSmallintParameter(command, "attributeLevel5", item.AttributeLevel5);
        command.Parameters.AddWithValue("itemQuality", item.Quality);
        command.Parameters.AddWithValue("itemGrade", item.Grade);
        command.Parameters.AddWithValue("bound", item.Bound);
        command.Parameters.AddWithValue("stack", item.Stack);
        command.Parameters.AddWithValue("itemExp", item.Exp);
        command.Parameters.AddWithValue("holySuitCode", item.HolySuitCode);
        AddHolyStoneParameters(command, item);
    }

    internal static void AddHolyStoneParameters(NpgsqlCommand command, CompactItemEntry item)
    {
        command.Parameters.AddWithValue(
            "holySocketCount",
            Math.Clamp(item.SocketCount, (short)0, (short)HolyStoneItemMutator.MaxSockets));
        AddNullableSmallintParameter(command, "holySocket1EffectId", item.Socket1EffectId);
        AddNullableSmallintParameter(command, "holySocket1Level", item.Socket1Level);
        AddNullableSmallintParameter(command, "holySocket2EffectId", item.Socket2EffectId);
        AddNullableSmallintParameter(command, "holySocket2Level", item.Socket2Level);
        AddNullableSmallintParameter(command, "holySocket3EffectId", item.Socket3EffectId);
        AddNullableSmallintParameter(command, "holySocket3Level", item.Socket3Level);
        AddNullableSmallintParameter(command, "holySocket4EffectId", item.Socket4EffectId);
        AddNullableSmallintParameter(command, "holySocket4Level", item.Socket4Level);
        AddNullableSmallintParameter(command, "holySocket5EffectId", item.Socket5EffectId);
        AddNullableSmallintParameter(command, "holySocket5Level", item.Socket5Level);
        AddNullableSmallintParameter(command, "holySocket6EffectId", item.Socket6EffectId);
        AddNullableSmallintParameter(command, "holySocket6Level", item.Socket6Level);
        AddNullableSmallintParameter(command, "holySocket1Value", item.Socket1Value);
        AddNullableSmallintParameter(command, "holySocket2Value", item.Socket2Value);
        AddNullableSmallintParameter(command, "holySocket3Value", item.Socket3Value);
        AddNullableSmallintParameter(command, "holySocket4Value", item.Socket4Value);
    }

    private static void AddAttributeParameter(NpgsqlCommand command, string name, int? attributeId)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Smallint)
        {
            Value = attributeId is >= 0 ? (short)attributeId.Value : DBNull.Value
        });
    }

    private static void AddNullableSmallintParameter(NpgsqlCommand command, string name, short? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Smallint)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });
    }

}
