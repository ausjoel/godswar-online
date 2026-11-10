using Godswar.Server.Application.EventsNpc;
using Godswar.Server.Application.Finalise2;

namespace Godswar.Server.Game;

/// <summary>
/// Treasure Map digs (4470 / 4471). Each map row carries one dig spot (ItemPlaceLimit Place ID) in
/// <c>holy_suit_code</c>, stamped by the trigger in <see cref="TreasureMapPlaces.StampSql"/>; the client shows it
/// in the tooltip and only sends the right-click within 10 units of it. The server checks the spot again, then
/// takes that one map and gives one treasure (the existing GENERATED Random Quest loot table). No pop-up follows
/// (the admin's rule); one info line says what happened. ROUND3: after a dig, its guardian(s) appear
/// (GameClientHandler.TreasureGuardians.cs, switch item.treasure_guardians).
/// Switch: item.treasure_map_digs (default 1); 0 = the old EventsNpc handler.
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryDigTreasureMapAsync(int slot, int itemId, CancellationToken cancellationToken)
    {
        if (!TreasureMapPlaces.IsTreasureMap(itemId) || !Finalise2Rates.On(Finalise2RateKeys.TreasureMapDigs) ||
            _character is not { } character || _account is null)
        {
            return false;
        }

        var map = character.CurrentMap;
        var x = character.PositionX;
        var z = character.PositionZ;
        var (lootId, lootCount) = EventsNpcPolicy.DrawTreasure(Random.Shared.Next());
        TreasureMapPlace place = default;
        var outcome = await RunEventsNpcAsync(async tx =>
        {
            // The used map is the one in the clicked slot: its own Place decides, not any other map in the bag.
            if (await tx.ScalarAsync("""
                    SELECT holy_suit_code FROM public.character_items
                    WHERE user_id = @c AND item_location = 1 AND slot_index = @s AND prop_id = @p FOR UPDATE;
                    """, ("s", (short)slot), ("p", itemId)) is not int placeId)
            {
                return "no-map";
            }

            if (!TreasureMapPlaces.TryGet(placeId, out place))
            {
                return "no-place";
            }

            if (!TreasureMapPlaces.IsAtPlace(place, map, x, z))
            {
                return "elsewhere";
            }

            // Maps do not stack (Overlap 1): delete exactly this row, then grant; a full bag rolls both back.
            var deleted = await tx.ScalarAsync("""
                DELETE FROM public.character_items
                WHERE user_id = @c AND item_location = 1 AND slot_index = @s AND prop_id = @p RETURNING id;
                """, ("s", (short)slot), ("p", itemId));
            if (deleted is null || !await tx.GrantAsync(lootId, lootCount, bound: null))
            {
                tx.Rollback();
                return "bag-full";
            }

            return "dug";
        }, "unavailable", cancellationToken);

        Console.WriteLine($"[treasure-map] character={character.Name} item={itemId} slot={slot} map={map} " +
            $"x={x:F1} z={z:F1} place={place.PlaceId} region={place.Region} placeMap={place.MapId} outcome={outcome} loot={lootId}x{lootCount}");
        switch (outcome)
        {
            case "dug":
                await RefreshAfterEventsNpcAsync([], cancellationToken);
                await SendGainLogAsync($"Treasure found: {GainItemName((uint)lootId)} x{lootCount}", cancellationToken);
                await TrySpawnTreasureGuardiansAsync(place.PlaceId, cancellationToken); // ROUND3 summons
                break;
            case "elsewhere":
                await SendKitBagRefreshAsync(cancellationToken);
                await SendInfoLineAsync($"The treasure is buried in {TreasureMapPlaces.RegionName(place.Region)} at " +
                    $"({place.X}, {place.Z}). Stand on that spot to dig.", InfoTabOther, cancellationToken);
                break;
            case "bag-full":
                await SendKitBagRefreshAsync(cancellationToken);
                await SendInfoLineAsync("Make room in your bag to dig up the treasure.", InfoTabOther, cancellationToken);
                break;
            default:
                // no-map / no-place / unavailable: the map stays; the trigger stamps an unstamped map on its next write.
                await SendKitBagRefreshAsync(cancellationToken);
                break;
        }

        return true;
    }
}
