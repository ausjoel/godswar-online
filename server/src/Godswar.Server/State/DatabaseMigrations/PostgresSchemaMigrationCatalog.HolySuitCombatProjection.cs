namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateHolySuitCombatProjection() => new(
        "20260916_151_holy_suit_combat_projection",
        "Match Holy Suit equipment bonuses and cumulative points to the client progression",
        """
        CREATE FUNCTION public.holy_suit_progression_points(p_code integer)
        RETURNS integer
        LANGUAGE sql IMMUTABLE PARALLEL SAFE
        AS $holy_suit_progression_points$
            SELECT CASE
                WHEN p_code / 100 BETWEEN 1 AND 7 AND p_code % 100 BETWEEN 1 AND 10
                    THEN (p_code / 100 - 1) * 10 + p_code % 100
                WHEN p_code / 100 = 8 AND p_code % 100 BETWEEN 1 AND 10
                    THEN (ARRAY[71,73,75,77,79,82,84,86,88,90])[p_code % 100]
                ELSE 0
            END;
        $holy_suit_progression_points$;

        COMMENT ON FUNCTION public.holy_suit_progression_points(integer) IS
            'Validated cumulative Holy Suit points and base-equipment bonus percent. Tiers 1..7 grant 1..70; Divinium levels 1..10 grant 71,73,75,77,79,82,84,86,88,90. Common or invalid codes grant zero.';

        CREATE OR REPLACE FUNCTION public.recompute_character_holy_suit_points(p_character_id integer)
        RETURNS integer
        LANGUAGE plpgsql
        AS $recompute_character_holy_suit_points$
        DECLARE
            calculated_points integer;
        BEGIN
            SELECT COALESCE(SUM(public.holy_suit_progression_points(item.holy_suit_code)), 0)::integer
            INTO calculated_points
            FROM public.character_items item
            WHERE item.user_id = p_character_id
              AND item.item_location = 0
              AND item.slot_index BETWEEN 0 AND 11;

            UPDATE public.character_base character_row
            SET holy_suit_points = calculated_points
            WHERE character_row.id = p_character_id;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'unknown character %', p_character_id USING ERRCODE = '23503';
            END IF;
            RETURN calculated_points;
        END
        $recompute_character_holy_suit_points$;

        UPDATE public.character_base character_row
        SET holy_suit_points = COALESCE((
            SELECT SUM(public.holy_suit_progression_points(item.holy_suit_code))::integer
            FROM public.character_items item
            WHERE item.user_id = character_row.id
              AND item.item_location = 0
              AND item.slot_index BETWEEN 0 AND 11
        ), 0);

        COMMENT ON FUNCTION public.recompute_character_holy_suit_points(integer) IS
            'Explicitly recomputes cumulative 0..1080 Holy Suit effect points from equipped regular slots 0..11. Called after committed equipment mutations; bag and non-regular slots do not contribute.';
        """);
}
