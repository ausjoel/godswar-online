using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// ECONOMY r11: both Pet Merchants sell the Greek Steed (6000) for 15,000 silver at the end of tab 1, and every
// captured listing keeps its (category, index), so purchases of existing items resolve exactly as before.
internal static class EconomyMountChecks
{
    public const string CheckName = "Economy r11 Pet Merchant Greek Steed listing";

    public static Task RunAsync()
    {
        Check.True(PacketBuilder.PetMerchantSteedEnabled, "the steed listing is on with GODSWAR_PET_MERCHANT_STEED=1 (RELEASE: opt-in; Program.Main sets it for the checks)");
        // (service, category, index, item, price) from the captured shelves (Athens PetMerchant, Sparta SpartaPetMerchant).
        (CapitalNpcServiceKind Service, int Category, int Index, uint Item, int Price)[] captured =
        [
            (CapitalNpcServiceKind.PetMerchant, 0, 0, 10000, 600),
            (CapitalNpcServiceKind.PetMerchant, 0, 9, 10061, 3500),
            (CapitalNpcServiceKind.PetMerchant, 0, 10, 10084, 200000),
            (CapitalNpcServiceKind.PetMerchant, 1, 0, 10200, 60000),
            (CapitalNpcServiceKind.PetMerchant, 1, 32, 10590, 100000),
            (CapitalNpcServiceKind.PetMerchant, 2, 33, 10740, 100000),
            (CapitalNpcServiceKind.SpartaPetMerchant, 0, 0, 10000, 600),
            (CapitalNpcServiceKind.SpartaPetMerchant, 0, 16, 10080, 500),
            (CapitalNpcServiceKind.SpartaPetMerchant, 0, 19, 10061, 3500),
            (CapitalNpcServiceKind.SpartaPetMerchant, 1, 32, 10590, 100000),
            (CapitalNpcServiceKind.SpartaPetMerchant, 2, 33, 10740, 100000)
        ];
        foreach (var listing in captured)
        {
            Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(listing.Service, listing.Category, listing.Index, listing.Item, out var offer) &&
                offer.UnitPrice == listing.Price && offer.Currency == CapitalNpcShopCurrency.Silver,
                $"{listing.Service} tab {listing.Category} #{listing.Index} is still item {listing.Item} at {listing.Price} silver");
        }

        foreach (var (service, index) in new[] { (CapitalNpcServiceKind.PetMerchant, 11), (CapitalNpcServiceKind.SpartaPetMerchant, 20) })
        {
            Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(service, 0, index, 6000, out var steed) &&
                steed.UnitPrice == 15_000 && steed.Currency == CapitalNpcShopCurrency.Silver && steed.Item.Id == 6000,
                $"{service} sells the Greek Steed for 15,000 silver at tab 1 #{index}");
            Check.True(!PacketBuilder.TryResolveCapitalNpcShopOffer(service, 0, index + 1, 6000, out _),
                $"{service} lists the steed once");
            var frames = PacketBuilder.CapitalNpcShopCatalog(5459, new CapitalNpcShopBalances(100_000, 0, 0), service);
            var offset = 0;
            var listed = 0;
            while (offset < frames.Length)
            {
                var length = BitConverter.ToUInt16(frames, offset);
                Check.True(length >= 16 && (length - 16) % 88 == 0 && frames[offset + 10] <= 16 &&
                    16 + frames[offset + 10] * 88 <= length, $"{service} frame at {offset} is well formed");
                for (var record = 0; record < frames[offset + 10]; record++)
                    if (BitConverter.ToUInt32(frames, offset + 16 + record * 88) == 6000) listed++;
                offset += length;
            }

            Check.Equal(1, listed, $"{service} catalogue carries exactly one steed record");
        }

        return Task.CompletedTask;
    }
}
