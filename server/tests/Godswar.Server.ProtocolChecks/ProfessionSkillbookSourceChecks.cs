using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// PROF skillbook source (2026-11-09): the Profession Skillbook (4268) that the Profession Mentor consumes is buyable in
// both capitals. GENERATED listing: the Scholarship Recipe Vendor (Athens_137 / Sparta_137) sells it for 10,000 silver
// at the end of tab 1 (category 0, index 32); every captured listing keeps its (category, index).
internal static class ProfessionSkillbookSourceChecks
{
    public const string CheckName = "PROF skillbook: Profession Skillbook 4268 is buyable in Athens and Sparta";

    public static Task RunAsync()
    {
        var definition = ServerRateCatalog.Definitions.Single(static d => d.Key == Finalise2RateKeys.ProfessionSkillbookShelf);
        Check.Equal(1m, definition.DefaultValue, "profession.skillbook_shelf defaults to 1");
        Check.Equal(Finalise2Rates.Category, definition.Category, "profession.skillbook_shelf is on the editor's finalise2 rates page");
        Check.True(PacketBuilder.ProfessionSkillbookShelfEnabled, "with no rate row the skillbook listing is on");
        Check.Equal(4268, ProfessionTables.ProfessionSkillbookItem, "the Mentor consumes item 4268");
        const CapitalNpcServiceKind kind = CapitalNpcServiceKind.AthensScholarshipRecipeVendor;
        const int index = 32;

        // (category, index, item, price) captured on the Athens Scholarship Recipe Vendor shelf.
        (int Category, int Index, uint Item, int Price)[] captured =
        [
            (0, 0, 13600, 1856),
            (0, 16, 13616, 17546),
            (1, 0, 13632, 44222),
            (2, 16, 13680, 360604)
        ];
        foreach (var listing in captured)
        {
            Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(kind, listing.Category, listing.Index, listing.Item, out var offer) &&
                offer.UnitPrice == listing.Price && offer.Currency == CapitalNpcShopCurrency.Silver,
                $"tab {listing.Category + 1} #{listing.Index} is still item {listing.Item} at {listing.Price} silver");
        }

        Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(kind, 0, index, 4268, out var book) &&
            book.UnitPrice == 10_000 && book.Currency == CapitalNpcShopCurrency.Silver && book.Item.Id == 4268,
            "the Scholarship Recipe Vendor sells the Profession Skillbook for 10,000 silver at tab 1 #32");
        Check.True(!PacketBuilder.TryResolveCapitalNpcShopOffer(kind, 0, index + 1, 4268, out _),
            "the skillbook is listed once");

        foreach (var (key, npc) in new[] { ("Athens_137", 5275u), ("Sparta_137", 5134u) })
        {
            Check.True(CapitalNpcServiceProtocol.TryResolve(key, npc, out var resolved) && resolved == kind &&
                CapitalNpcServiceProtocol.IsShop(resolved), $"{key}/{npc} is the Scholarship Recipe Vendor shop");
            var frames = PacketBuilder.CapitalNpcShopCatalog(npc, new CapitalNpcShopBalances(100_000, 0, 0), resolved);
            var offset = 0;
            var listed = 0;
            var tabRecords = 0;
            var lastTabFrameFlag = -1;
            while (offset < frames.Length)
            {
                var length = BitConverter.ToUInt16(frames, offset);
                Check.True(length >= 16 && (length - 16) % 88 == 0 && frames[offset + 10] <= 16 &&
                    16 + frames[offset + 10] * 88 <= length, $"{key} frame at {offset} is well formed");
                for (var record = 0; record < frames[offset + 10]; record++)
                {
                    if (BitConverter.ToUInt32(frames, offset + 16 + record * 88) == 4268)
                    {
                        listed++;
                        Check.Equal(0, (int)frames[offset + 8], $"{key} lists the skillbook on tab 1");
                        Check.Equal(index, tabRecords + record, $"{key} lists the skillbook at tab 1 #{index}");
                        lastTabFrameFlag = frames[offset + 11];
                    }
                }

                if (frames[offset + 8] == 0)
                {
                    tabRecords += frames[offset + 10];
                }
                offset += length;
            }

            Check.Equal(1, listed, $"{key} catalogue carries exactly one skillbook record");
            Check.Equal(index + 1, tabRecords, $"{key} tab 1 has 32 captured listings plus the skillbook");
            Check.Equal(0, lastTabFrameFlag, $"{key} skillbook frame continues tab 1 (header byte 11 = 0)");
        }

        return Task.CompletedTask;
    }
}
