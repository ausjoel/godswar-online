namespace Godswar.Server.State;

/// <summary>
/// Native Holy Suit Effect applies only to thirteen integer base fields of
/// each equipped item. Truncate each bonus before combining item statistics.
/// Appended attributes and other character bonuses never pass through here.
/// </summary>
internal static class PostgresCharacterHolySuitProjectionSql
{
    public const string BaseValueLateralJoin = """
        CROSS JOIN LATERAL (
            SELECT COALESCE(NULLIF(stat_values.values[
                LEAST(GREATEST(equipment.item_quality::integer, 1),
                    array_length(stat_values.values, 1))
            ], '')::numeric, 0::numeric) AS value
        ) holy_suit_base
        """;

    public const string AdjustedBaseValue = """
        (holy_suit_base.value + CASE WHEN stat.source_key IN (
            'MaxHP', 'MaxMP', 'Attack', 'Defence', 'Hit', 'Miss',
            'MagicAk', 'MagicRec', 'State', 'StateImmunity',
            'InjureImbibe', 'FuryAddAk', 'FuryAddRec')
        THEN TRUNC(holy_suit_base.value *
            public.holy_suit_progression_points(equipment.holy_suit_code) / 100.0)
        ELSE 0::numeric END)
        """;
}
