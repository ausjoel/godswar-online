using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The Lelantine Farm Quartermaster is a shop, not a dialogue: the capture
/// answers its click with flags 4 and then streams an opcode-10071 catalogue.
/// These checks pin that catalogue to the captured bytes.
/// </summary>
internal static class LelantineFarmShopChecks
{
    public const string CheckName = "Lelantine Farm quartermaster shop catalogue";

    /// <summary>Length of the single captured opcode-10071 frame.</summary>
    private const int CapturedFrameLength = 892;

    /// <summary>The captured frame's listing count.</summary>
    private const int CapturedListingCount = 10;

    /// <summary>
    /// The client's own silver currency byte. The captured frame carries 1,
    /// which that table has no entry for, so the catalogue rewrites byte 9.
    /// </summary>
    private const byte ClientSilverCurrencyByte = 3;

    /// <summary>The captured frame's category byte.</summary>
    private const byte CapturedCategory = 0;

    public static Task RunAsync()
    {
        CheckQuartermasterIsAShop();
        CheckCatalogMatchesTheCapture();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The quartermaster must resolve as a shop service and price in silver,
    /// because the captured frames carry currency byte 1.
    /// </summary>
    private static void CheckQuartermasterIsAShop()
    {
        Check.True(
            CapitalNpcServiceProtocol.TryResolve(
                "Lelantine_Farm_004",
                5616u,
                out var athenian) &&
            athenian == CapitalNpcServiceKind.LelantineFarmQuartermaster,
            "the Athenian quartermaster resolves as the farm shop");
        Check.True(
            CapitalNpcServiceProtocol.TryResolve(
                "Lelantine_Farm_007",
                5621u,
                out var spartan) &&
            spartan == CapitalNpcServiceKind.LelantineFarmQuartermaster,
            "the Spartan quartermaster resolves as the farm shop");
        Check.True(
            CapitalNpcServiceProtocol.IsShop(
                CapitalNpcServiceKind.LelantineFarmQuartermaster),
            "the farm shop is recognised as a shop");
        Check.True(
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.LelantineFarmQuartermaster,
                out var currency),
            "the farm shop resolves a currency");
        // The captured frame's currency byte is 1, which the client's own shop
        // table reads as silver; the enum's own ordinal is unrelated to that
        // byte, so the two are asserted separately.
        Check.True(
            currency == CapitalNpcShopCurrency.Silver,
            "farm shop charges silver");

        // The quartermaster must NOT answer the activity dialog: that window
        // belongs to the Advance Troop Captain alone.
        Check.True(
            LelantineFarmProtocol.IsQuartermaster("Lelantine_Farm_004") &&
            LelantineFarmProtocol.IsQuartermaster("Lelantine_Farm_007") &&
            !LelantineFarmProtocol.IsAdvanceTroopCaptain(
                "Lelantine_Farm_004"),
            "the quartermaster is split out from the dialog endpoint");
        Check.True(
            LelantineFarmProtocol.IsAdvanceTroopCaptain("Lelantine_Farm_003") &&
            LelantineFarmProtocol.IsAdvanceTroopCaptain("Lelantine_Farm_006") &&
            !LelantineFarmProtocol.IsQuartermaster("Lelantine_Farm_003"),
            "only the two captains answer the activity dialog");
    }

    /// <summary>
    /// The embedded catalogue must inflate to exactly the captured frame, with
    /// the captured header, listing count and first listing.
    /// </summary>
    private static void CheckCatalogMatchesTheCapture()
    {
        var catalog = PacketBuilder.CapitalNpcShopCatalog(
            npcId: 5616u,
            new CapitalNpcShopBalances(Silver: 0, Gold: 0, BindingGold: 0),
            CapitalNpcServiceKind.LelantineFarmQuartermaster);

        Check.Equal(
            CapturedFrameLength,
            catalog.Length,
            "farm shop catalogue is the captured frame length");

        var declared = BinaryPrimitives.ReadUInt16LittleEndian(catalog);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(
            catalog.AsSpan(2, 2));
        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(
            catalog.AsSpan(4, 4));
        Check.Equal(
            CapturedFrameLength,
            (int)declared,
            "farm shop frame declares its own length");
        Check.Equal(
            (ushort)10071,
            opcode,
            "farm shop frame uses the npc shop catalogue opcode");
        Check.Equal(5616u, npcId, "farm shop frame carries the captured npc id");
        Check.Equal(
            CapturedCategory,
            catalog[8],
            "farm shop frame uses the captured category");
        Check.Equal(
            ClientSilverCurrencyByte,
            catalog[9],
            "farm shop frame is rewritten to the client's silver byte");
        Check.Equal(
            CapturedListingCount,
            (int)catalog[10],
            "farm shop frame lists the captured ten entries");
        Check.Equal(
            1,
            (int)catalog[11],
            "farm shop frame is a fresh list");

        // The first listing is the wooden tuck net, and the five nets repeat
        // once each in the order the capture holds them.
        var expectedItems = new uint[]
        {
            10080u, 10081u, 10082u, 10083u, 10084u,
            10080u, 10081u, 10082u, 10083u, 10084u
        };
        for (var index = 0; index < expectedItems.Length; index++)
        {
            var itemId = BinaryPrimitives.ReadUInt32LittleEndian(
                catalog.AsSpan(16 + (index * 88), 4));
            Check.Equal(
                expectedItems[index],
                itemId,
                $"farm shop listing {index} item id");
        }
    }
}
