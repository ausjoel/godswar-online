using Godswar.Server.Application.Finalise2;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // PROF skillbook source (2026-11-09): the Profession Mentor teaches a profession for one Profession Skillbook
    // (4268), but no shop, drop or quest gave one, so nobody could learn a profession without a GM.
    // Retail had NO shop source and no price: the IGG guide (Reference/gw.igg.com-2022-01-30/guide/guide.php__acid=210,
    // "Profession System") says the book came from the Lvl 70 main quest (Lvl 40 on event/newcontent) or from a
    // Scholar (Lifing_Blueprint 40000, the Scholarship starter blueprint, which needs a book first). Neither the
    // client nor this server has a quest that rewards 4268, and no captured shelf lists it.
    // GENERATED: the Scholarship Recipe Vendor (Athens_137, and Sparta_137 which shares the shelf since PR #103) sells
    // it for 10,000 silver, appended after the last listing of its first tab (category 0, index 32), so every captured
    // listing keeps its (category, index). Tab 1 already holds two full frames, so the listing travels in a third
    // frame with header byte 11 = 0, as every later frame of a captured tab does (the captured Bound Gold Vendor's
    // tab 3 carries 40 records over five frames, so a tab is not capped at 32). The copied record is unbound (byte 26 = 0).
    // Hot switch: Finalise2 rate profession.skillbook_shelf (default 1, editor rates page). It is read every time the
    // shelf is sent or a purchase is resolved, so a change needs no restart (a window already open keeps its listing;
    // buying slot 32 after the switch went to 0 simply fails). An npc_shop_overrides list for the service still
    // replaces the whole shelf, as before.
    internal const uint ProfessionSkillbookItemId = 4268;
    internal const uint ProfessionSkillbookPrice = 10_000;
    internal const byte ProfessionSkillbookCategory = 0;

    internal static bool ProfessionSkillbookShelfEnabled =>
        Finalise2Rates.On(Finalise2RateKeys.ProfessionSkillbookShelf);

    // The captured shelf with the skillbook appended; built once, chosen per call by the switch.
    private static readonly Lazy<byte[]> ScholarshipRecipeVendorWithSkillbookCatalog = new(
        () => AppendSteedShelfListing(ScholarshipRecipeVendorCatalog.Value, ProfessionSkillbookCategory,
            ProfessionSkillbookItemId, ProfessionSkillbookPrice));

    private static byte[] ScholarshipRecipeVendorShelf() =>
        ProfessionSkillbookShelfEnabled
            ? ScholarshipRecipeVendorWithSkillbookCatalog.Value
            : ScholarshipRecipeVendorCatalog.Value;
}
