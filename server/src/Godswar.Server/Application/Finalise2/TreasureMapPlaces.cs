namespace Godswar.Server.Application.Finalise2;

/// <summary>
/// One treasure-map dig spot (client ItemDynamicAttribute.xml ItemPlaceLimit/Place + Coordinate/xz).
/// <see cref="Region"/> is the file's <c>Map</c> value, which is the client's REGION numbering (Text/QuestRegion.dat:
/// 0 Athens, 1 Athens' Suburb, 50 Sparta, ...), NOT the server map id. <see cref="MapId"/> is the server map id
/// the client's own Settings/Sys/MapIdToNameConfig.ini pairs with it ("MapID&lt;server&gt; = Name,&lt;region&gt;");
/// the client's use check (0x442280) translates the player's map through that same table. X/Z are world
/// coordinates: the client compares them directly with the player's position floats.
/// </summary>
internal readonly record struct TreasureMapPlace(int PlaceId, short Region, int X, int Z)
{
    /// <summary>The server map id of <see cref="Region"/>, or -1 when the region has no map in the table.</summary>
    public short MapId => TreasureMapPlaces.ServerMapIdForRegion(Region);
}

/// <summary>
/// Treasure Map digs (4470 / 4471, ItemBaseAttribute PlaceLimit="1"). How the stock client works (read from
/// GodsWar.exe 1.4.4, 2026-11-08):
/// <list type="bullet">
/// <item>Each map carries ONE Place ID in its item record's 16-bit word at offset 32 (the word holy suits use;
/// the server's <c>character_items.holy_suit_code</c>). Client accessor 0x435090 copies it to +0x20.</item>
/// <item>The tooltip (0x587780) looks the Place up in the ItemPlaceLimit table and prints "Random Maps", the map
/// name and "(x,z)" from its Coordinate row. Its colour comes from the same check as the right-click.</item>
/// <item>Right-click (0x4afc5c -> 0x442280): the client sends the use only when the player is on the Place's map
/// and dx*dx + dz*dz &lt; 100 (within 10 units, no square root). With Place 0 (an unstamped map) the lookup fails
/// and the client refuses the right-click itself, so no packet ever reaches the server.</item>
/// </list>
/// The file has no link from a Place to what a dig gives; ItemRefreshMonster 32000-32056 (one monster at the
/// player, NoteType 1) and 33000-35505 (five bosses in regions 100-104/152, NoteType 2) are the dig spawns retail
/// used. ROUND3: GameClientHandler.TreasureGuardians.cs spawns them after a dig (ItemRefreshMonsters).
/// </summary>
internal static partial class TreasureMapPlaces
{
    public const int AthensTreasureMap = 4470;
    public const int SpartaTreasureMap = 4471;

    /// <summary>The client's own radius: dx*dx + dz*dz &lt; 100.</summary>
    public const int ClientRadiusSquared = 100;

    /// <summary>
    /// GENERATED: the server accepts up to 15 units (225) so a position update still in flight does not refuse a
    /// dig the client already allowed.
    /// </summary>
    public const int ServerRadiusSquared = 225;

    // Lazy: All lives in the generated partial file, and static initialisers across partial files run in no set order.
    private static Dictionary<int, TreasureMapPlace>? _byId;

    private static Dictionary<int, TreasureMapPlace> ById => _byId ??= All.ToDictionary(static place => place.PlaceId);

    public static bool IsTreasureMap(int itemId) => itemId is AthensTreasureMap or SpartaTreasureMap;

    public static bool TryGet(int placeId, out TreasureMapPlace place) => ById.TryGetValue(placeId, out place);

    // GENERATED: the wiki says the location is random ("a starting area or a high-level zone"), so a map may point
    // at any of the 96 places. The Place is derived from the row id (SQL: All[(hashint8(id) & 0x7fffffff) % 96]),
    // not a fresh random number, so a map keeps its spot even if some write path stores 0 again.

    /// <summary>True when the player (server map id + world position) stands on the Place within the server radius.</summary>
    public static bool IsAtPlace(TreasureMapPlace place, int mapId, float x, float z)
    {
        if (place.MapId < 0 || place.MapId != mapId)
        {
            return false;
        }

        double dx = x - place.X;
        double dz = z - place.Z;
        return dx * dx + dz * dz <= ServerRadiusSquared;
    }

    /// <summary>
    /// Region -> server map id, from the client's MapIdToNameConfig.ini (MapID0 = Sparta,50; MapID1 = Athens,0;
    /// MapID2 = Athens_Newbie,1; MapID3 = Parnitha_1,3; MapID4 = Sparta_Newbie,51; MapID5 = Nemea_1,53;
    /// MapID8 = Thermopylae_All,152; MapID11 = Marathon_All,2; MapID12 = Parnitha_2,4; MapID13 = Peloponnese_All,52;
    /// MapID14 = Nemea_2,54; MapID15-19 = Derveni/Argolis/Isthmus/Megara/Plataea, 100-104). These agree with the
    /// server's own ids (Marathon 11, Peloponnese 13, Thermopylae 8, Derveni 15).
    /// </summary>
    public static short ServerMapIdForRegion(short region) => region switch
    {
        50 => 0,
        0 => 1,
        1 => 2,
        3 => 3,
        51 => 4,
        53 => 5,
        152 => 8,
        2 => 11,
        4 => 12,
        52 => 13,
        54 => 14,
        100 => 15,
        101 => 16,
        102 => 17,
        103 => 18,
        104 => 19,
        _ => -1
    };

    /// <summary>Kept for callers of the first version: the name of a REGION id (same as <see cref="RegionName"/>).</summary>
    public static string MapName(short region) => RegionName(region);

    /// <summary>Region names from the client's Text/QuestRegion.dat (the name the tooltip shows).</summary>
    public static string RegionName(short region) => region switch
    {
        0 => "Athens",
        1 => "Athens' Suburb",
        2 => "Marathon",
        3 => "Parnitha",
        4 => "Parnitha Port",
        50 => "Sparta",
        51 => "Sparta's Suburb",
        52 => "Peloponnese",
        53 => "Nemea",
        54 => "Nemea Forest",
        100 => "Derveni",
        101 => "Argolis",
        102 => "Isthmus",
        103 => "Megara",
        104 => "Plataea",
        152 => "Thermopylae",
        _ => $"region {region}"
    };

    /// <summary>
    /// Runtime DDL (not a registered migration, like the other Finalise2 tables): a trigger that stamps a Place
    /// on every Treasure Map row whose word is still 0, whatever path wrote it (quest reward, GM grant, trade,
    /// mail, warehouse), and a one-off backfill of maps already in bags and warehouses. Run as its own command
    /// (a failure here must not take the EventsNpc tables down); the trigger and backfill run once per database.
    /// </summary>
    public static string StampSql()
    {
        var ids = string.Join(",", All.Select(static place => place.PlaceId));
        return $$"""

        SET lock_timeout = '3s';
        -- Treasure Map digs (2026-11-08): stamp a dig spot (ItemPlaceLimit Place ID) on maps 4470/4471. The client
        -- reads it from the item record's word at offset 32 (holy_suit_code). Not a registered migration.
        CREATE OR REPLACE FUNCTION public.treasure_map_place_for(p_id bigint) RETURNS integer
        LANGUAGE sql IMMUTABLE AS $f$
            SELECT (ARRAY[{{ids}}]::integer[])[1 + ((hashint8(p_id) & 2147483647) % {{All.Length}})];
        $f$;
        CREATE OR REPLACE FUNCTION public.treasure_map_stamp() RETURNS trigger
        LANGUAGE plpgsql AS $f$
        DECLARE
            kept integer;
        BEGIN
            -- A stale in-memory save (word 0) must not move a map's spot: keep the stamp the row already has.
            IF TG_OP = 'UPDATE' AND OLD.prop_id = NEW.prop_id AND OLD.holy_suit_code <> 0 THEN
                NEW.holy_suit_code := OLD.holy_suit_code;
                RETURN NEW;
            END IF;
            IF TG_OP = 'INSERT' THEN
                -- INSERT ... ON CONFLICT upserts of the same map in the same slot keep the existing stamp too.
                SELECT holy_suit_code INTO kept FROM public.character_items
                WHERE user_id = NEW.user_id AND item_location = NEW.item_location
                  AND slot_index = NEW.slot_index AND prop_id = NEW.prop_id AND holy_suit_code <> 0
                LIMIT 1;
                IF FOUND THEN
                    NEW.holy_suit_code := kept;
                    RETURN NEW;
                END IF;
            END IF;
            NEW.holy_suit_code := public.treasure_map_place_for(NEW.id::bigint);
            RETURN NEW;
        END;
        $f$;
        -- The trigger and the backfill take a lock on / scan character_items, so they run once per database only.
        DO $do$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_treasure_map_stamp') THEN
                CREATE TRIGGER trg_treasure_map_stamp
                    BEFORE INSERT OR UPDATE OF prop_id, holy_suit_code ON public.character_items
                    FOR EACH ROW WHEN (NEW.prop_id IN ({{AthensTreasureMap}}, {{SpartaTreasureMap}}) AND NEW.holy_suit_code = 0)
                    EXECUTE FUNCTION public.treasure_map_stamp();
                UPDATE public.character_items SET holy_suit_code = public.treasure_map_place_for(id::bigint)
                    WHERE prop_id IN ({{AthensTreasureMap}}, {{SpartaTreasureMap}}) AND holy_suit_code = 0;
            END IF;
        END
        $do$;
        """;
    }
}
