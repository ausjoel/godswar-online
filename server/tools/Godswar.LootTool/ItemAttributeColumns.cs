using Npgsql;
using NpgsqlTypes;

namespace Godswar.LootTool;

/// <summary>
/// <c>monster_loot_rules</c> 与 <c>quest_reward_slots</c> 上那 12 列「物品实例属性」
/// （服务端迁移 20260927_214 加的，两张表列名完全一样）。
/// </summary>
/// <remarks>
/// 列名与读写放在一处，免得两个 Store 各写一份、哪天加列漏一个。
/// 顺序固定：品质、等级，然后 5 组「属性 id + 属性等级」。
/// </remarks>
internal static class ItemAttributeColumns
{
    public static readonly string[] Names =
    [
        "item_quality",
        "item_grade",
        "attribute1",
        "attribute_level1",
        "attribute2",
        "attribute_level2",
        "attribute3",
        "attribute_level3",
        "attribute4",
        "attribute_level4",
        "attribute5",
        "attribute_level5"
    ];

    /// <summary>12 列齐全才算「这张表支持配物品属性」（缺一列就整体当没有）。</summary>
    public static bool HasAll(IReadOnlySet<string> columns) =>
        Names.All(columns.Contains);

    /// <summary>从 <paramref name="firstOrdinal"/> 起读 12 个可空 smallint。</summary>
    public static ItemAttributeValues Read(NpgsqlDataReader reader, int firstOrdinal) =>
        new(
            ReadNullable(reader, firstOrdinal),
            ReadNullable(reader, firstOrdinal + 1),
            ReadNullable(reader, firstOrdinal + 2),
            ReadNullable(reader, firstOrdinal + 3),
            ReadNullable(reader, firstOrdinal + 4),
            ReadNullable(reader, firstOrdinal + 5),
            ReadNullable(reader, firstOrdinal + 6),
            ReadNullable(reader, firstOrdinal + 7),
            ReadNullable(reader, firstOrdinal + 8),
            ReadNullable(reader, firstOrdinal + 9),
            ReadNullable(reader, firstOrdinal + 10),
            ReadNullable(reader, firstOrdinal + 11));

    /// <summary>
    /// 12 个参数都显式声明 smallint：不配置必须是 NULL，
    /// 不能让 Npgsql 因为值是 DBNull 而推不出类型。
    /// </summary>
    public static void AddParameters(
        NpgsqlCommand command,
        ItemAttributeValues? attributes)
    {
        var values = attributes is null
            ? new short?[Names.Length]
            :
            [
                attributes.Quality,
                attributes.Grade,
                attributes.Attribute1,
                attributes.AttributeLevel1,
                attributes.Attribute2,
                attributes.AttributeLevel2,
                attributes.Attribute3,
                attributes.AttributeLevel3,
                attributes.Attribute4,
                attributes.AttributeLevel4,
                attributes.Attribute5,
                attributes.AttributeLevel5
            ];
        for (var index = 0; index < Names.Length; index++)
        {
            command.Parameters.Add(new NpgsqlParameter(Names[index], NpgsqlDbType.Smallint)
            {
                Value = values[index] is { } value ? value : DBNull.Value
            });
        }
    }

    private static short? ReadNullable(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);
}
