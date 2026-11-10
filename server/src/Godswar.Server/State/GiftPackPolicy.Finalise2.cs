namespace Godswar.Server.State;

// FINALISE2 (P4, 2026-11-05): gift packs from the admin's answers F-91, F-92, F-33 and F-32.
// Development/finalise2/p4-handlers/README.md lists every entry, its source and what is GENERATED.
internal static partial class GiftPackPolicy
{
    // FINALISE2: the six jades the Greater Pixie Bag text names (F-92). Even odds are GENERATED.
    // Properties, not fields: Packs (GiftPackPolicy.cs) is built during type initialisation, before
    // fields of this partial file are guaranteed to be set.
    private static GiftPackReward[] GreaterPixieJades =>
        [new(11053, 1), new(11054, 1), new(11057, 1), new(11060, 1), new(11050, 1), new(11055, 1)];

    // FINALISE2: GENERATED contents (F-33). The Simple Pixie Bag text says only "chances to get the pet
    // appearance modifying props": one of six other Magic Jades, even odds.
    // Properties, not fields: Packs (GiftPackPolicy.cs) is built during type initialisation, before
    // fields of this partial file are guaranteed to be set.
    private static GiftPackReward[] SimplePixieJades =>
        [new(11051, 1), new(11052, 1), new(11056, 1), new(11058, 1), new(11059, 1), new(11062, 1)];

    // FINALISE2: GENERATED contents (F-33). Sprite's Precious Collection ("the sea sprites in Vonaheim like to
    // collect rare things"): one sea or rare Magic Jade, weighted towards the sea ones.
    // Properties, not fields: Packs (GiftPackPolicy.cs) is built during type initialisation, before
    // fields of this partial file are guaranteed to be set.
    private static GiftPackReward[] SpritePreciousJades =>
        [new(11058, 1, Weight: 3), new(11085, 1, Weight: 3), new(11089, 1, Weight: 2), new(11061, 1, Weight: 1)];

    private static IEnumerable<KeyValuePair<uint, GiftPackDefinition>> Finalise2Packs =>
    [
        // FINALISE2 F-91: His-and-Her Clothing = the 30-day Lovers' Set (unisex) + Super Firework of Love, as the
        // item text lists. The database expiry trigger (trg_character_items_stamp_expiry) stamps the 30 days.
        new(3973, new(5214, [new(8218, 1), new(3950, 1)])),
        new(3974, new(5215, [new(8208, 1), new(3950, 1)])),
        // FINALISE2 F-92: Greater Pixie Bag (both ids) = one of the six named jades.
        new(11011, new(5512, [], [new(1, GreaterPixieJades)])),
        new(11013, new(5514, [], [new(1, GreaterPixieJades)])),
        // FINALISE2 F-33: GENERATED. 11014 is the same Simple Pixie Bag as 11012 (also sold), so it opens the same.
        new(11012, new(5513, [], [new(1, SimplePixieJades)])),
        new(11014, new(5515, [], [new(1, SimplePixieJades)])),
        new(11002, new(4799, [], [new(1, SpritePreciousJades)])),
        // FINALISE2 F-90: vouchers whose reward the text states exactly. The client gives them no skill (SkillId 0),
        // so a right-click redeems them; the NPCs the text names have no client exchange page.
        new(14013, new(0, [new(14012, 1)])),   // Mid-autumn voucher -> Mid-autumn Festival Gift Pack
        new(14053, new(0, [new(14054, 1)])),   // Holy Stone Package Voucher -> Holy Stone Package
        // FINALISE2 F-36: GENERATED. Coloured gems ("can be exchanged for high levelled <class> skillbooks"). The client
        // has no exchange page at the skill trainer, so a right-click exchanges the gem for one random skill book of
        // its class with level requirement 90-110 (gameplay_skill_book_definitions of the 2026-11-05 base; the
        // 131+ books stay rare boss loot per F-10). Even odds.
        // Red Gem: Warrior -> one Lv90-110 skill book of that class.
        new(4341, new(0, [], [new(1, [new(5008, 1), new(5009, 1), new(5010, 1), new(5017, 1), new(5018, 1), new(5025, 1), new(5026, 1), new(5031, 1), new(5036, 1), new(5041, 1)])])),
        // Yellow Gem: Champion -> one Lv90-110 skill book of that class.
        new(4342, new(0, [], [new(1, [new(5208, 1), new(5209, 1), new(5210, 1), new(5217, 1), new(5218, 1), new(5225, 1), new(5226, 1), new(5233, 1), new(5234, 1), new(5239, 1), new(5244, 1)])])),
        // Blue Gem: Mage -> one Lv90-110 skill book of that class.
        new(4343, new(0, [], [new(1, [new(5408, 1), new(5409, 1), new(5410, 1), new(5417, 1), new(5418, 1), new(5425, 1), new(5426, 1), new(5431, 1), new(5435, 1), new(5436, 1), new(5441, 1), new(5444, 1)])])),
        // Green Gem: Priest -> one Lv90-110 skill book of that class.
        new(4344, new(0, [], [new(1, [new(5604, 1), new(5608, 1), new(5609, 1), new(5614, 1), new(5619, 1), new(5622, 1), new(5628, 1), new(5629, 1), new(5639, 1), new(5640, 1), new(5641, 1), new(5648, 1), new(5649, 1)])])),
        // FINALISE2 F-32: Private badges grant a title. The client title table (DesigName.dat) has no "Athenian
        // Private" / "Spartan Private", so they grant the nearest existing client titles, Athenian Soldier 2000 and
        // Spartan Soldier 2500 (GENERATED mapping). A new title id would draw nothing in the stock client.
        // The gift-pack receipt needs at least one item, so each badge also gives 1 Simple Firework (GENERATED).
        new(14029, new(5541, [new(3939, 1)], TitleId: 2000)),
        new(14030, new(5542, [new(3939, 1)], TitleId: 2500)),
    ];
}
