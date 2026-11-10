using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // W3 Magic Bell (F-136, 2026-11-09): the admin's first answer was "sold by the Pet Manager for 50,000 silver"
    // (GENERATED). The Pet Manager (Athens_088 / Sparta_088) is a dialogue-only npc with no shop window, so the bell
    // was listed by the two Pet Merchants (Athens_089, Sparta_089), both silver shelves, at the end of their first tab
    // like the Greek Steed. F-143 (the admin 2026-11-09: "30g in mall or in some loot tables 0.5%"): the captured Item Mall
    // already sells the bell (both camps, pets tab = category 3, index 8, 30 Gold; bought on the item-sweep rig
    // 2026-11-04, Development/item-sweep/shops.csv), so this silver listing is now behind pet.magic_bell_merchant
    // (default 0). Price: pet.magic_bell_price. An editor shelf override (npc_shop_overrides) still replaces the
    // whole shelf, bell included.
    private static readonly ConditionalWeakTable<byte[], ConcurrentDictionary<uint, byte[]>> MagicBellCatalogs = new();

    internal static bool IsMagicBellShelf(CapitalNpcServiceKind service) =>
        service is CapitalNpcServiceKind.PetMerchant or CapitalNpcServiceKind.SpartaPetMerchant;

    private static byte[] WithMagicBellListing(CapitalNpcServiceKind service, byte[] catalog)
    {
        if (!IsMagicBellShelf(service) || !Finalise2Rates.On(Finalise2RateKeys.MagicBell) ||
            !Finalise2Rates.On(Finalise2RateKeys.MagicBellMerchant)) // F-143
        {
            return catalog;
        }

        var price = (uint)Math.Max(1, Finalise2Rates.Amount(Finalise2RateKeys.MagicBellPrice));
        return MagicBellCatalogs.GetOrCreateValue(catalog).GetOrAdd(
            price,
            p => AppendSteedShelfListing(catalog, category: 0, (uint)PetQuestDispatchPolicy.MagicBellItemId, p));
    }
}
