using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CapitalNpcServiceProtocolChecks
{
    private static void CheckCapturedShopOfferAuthority()
    {
        Check.True(
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.PetMerchant,
                category: 0,
                listingIndex: 0,
                expectedItemId: 10000,
                out var petOffer) &&
            petOffer.UnitPrice == 600 &&
            petOffer.Currency == CapitalNpcShopCurrency.Silver &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.PetMerchant,
                category: 1,
                listingIndex: 0,
                expectedItemId: 10200,
                out var petSecondCategoryOffer) &&
            petSecondCategoryOffer.Currency ==
                CapitalNpcShopCurrency.Silver &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.PetMerchant,
                category: 1,
                listingIndex: 16,
                expectedItemId: 10224,
                out var petSecondCategorySecondFrameOffer) &&
            petSecondCategorySecondFrameOffer.Currency ==
                CapitalNpcShopCurrency.Silver &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.SkillVendor,
                category: 0,
                listingIndex: 0,
                expectedItemId: 5445,
                out var skillOffer) &&
            skillOffer.UnitPrice == 1 &&
            skillOffer.Currency == CapitalNpcShopCurrency.BindingGold &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.SkillVendor,
                category: 2,
                listingIndex: 0,
                expectedItemId: 5042,
                out var skillThirdCategoryOffer) &&
            skillThirdCategoryOffer.Currency ==
                CapitalNpcShopCurrency.BindingGold &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.SkillVendor,
                category: 2,
                listingIndex: 16,
                expectedItemId: 5024,
                out var skillThirdCategoryContinuationOffer) &&
            skillThirdCategoryContinuationOffer.Currency ==
                CapitalNpcShopCurrency.BindingGold &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.PropsVendor,
                category: 0,
                listingIndex: 0,
                expectedItemId: 3100,
                out var propsSilverOffer) &&
            propsSilverOffer.UnitPrice == 71 &&
            propsSilverOffer.Currency == CapitalNpcShopCurrency.Silver &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.PropsVendor,
                category: 1,
                listingIndex: 0,
                expectedItemId: 9010,
                out var propsBindingGoldOffer) &&
            propsBindingGoldOffer.UnitPrice == 20_000 &&
            propsBindingGoldOffer.Currency ==
                CapitalNpcShopCurrency.BindingGold,
            "captured Pet, Skill, and mixed-currency Props offers resolve " +
                "currency from their exact catalog frames");

        Check.True(
            !PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.PropsVendor,
                category: 1,
                listingIndex: 8,
                expectedItemId: 9010,
                out _),
            "shop listing indices are category-relative, not catalog-global");

        Check.True(
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.PropsVendor,
                category: 0,
                listingIndex: 0,
                expectedItemId: 4001,
                out var fiveMarkerOffer) &&
            fiveMarkerOffer.UnitPrice == 25 &&
            fiveMarkerOffer.Item.Stack == 1 &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.PropsVendor,
                category: 0,
                listingIndex: 5,
                expectedItemId: 4001,
                out var ninetyNineMarkerOffer) &&
            ninetyNineMarkerOffer.UnitPrice == 25 &&
            ninetyNineMarkerOffer.Item.Stack == 1,
            "new category segments reset listing indices while captured " +
                "5/99 stack markers cannot multiply the purchase quantity");
    }
}
