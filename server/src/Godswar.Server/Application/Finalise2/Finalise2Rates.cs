using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Finalise2;

// FINALISE2 (P4 item and NPC handlers, 2026-11-05): hot switches and amounts for the handlers built from the admin's
// answers (Development/finalise2/p4-handlers/README.md). Each switch at 0 gives the old behaviour back.
internal static class Finalise2RateKeys
{
    public const string BGoldTrader = "npc.bgold_trader";
    public const string BGoldTraderAmount = "npc.bgold_trader_amount";
    public const string MysteryMerchant = "npc.mystery_merchant";
    public const string CadianPlates = "npc.cadian_plates";
    public const string CadianPlateExperience = "npc.cadian_plate_exp";
    public const string WarSupplierBuyBack = "npc.war_supplier_buyback";
    public const string Vouchers = "item.finalise2_vouchers";
    public const string FactionChangeTeleport = "npc.faction_change_teleport";
    public const string GenderChangeTeleport = "npc.gender_change_teleport"; // F-83 follow-up
    public const string TalentReset = "item.talent_reset";
    public const string PetItems = "item.finalise2_pet_items";
    public const string HairItems = "item.hair_items";
    public const string QuestCapsTo200 = "quest.caps_121_140_to_200"; // FINALISE2b F-121
    public const string QuestCaps120To200 = "quest.caps_120_to_200"; // ROUND3 F-123
    public const string PersonalHelper = "npc.personal_helper"; // W2 F-27
    public const string MountFeeder = "npc.mount_feeder"; // W2 F-52
    public const string Apothecary = "npc.apothecary"; // W2 F-27
    public const string Lottery = "npc.lottery"; // W2 F-27
    public const string HolyStoneRefund = "npc.holy_stone_refund"; // F-27 / F-140 Holy Stone Redeemer's three pages
    public const string LotteryPrizeGold = "npc.lottery_prize_gold"; // W2 F-27, GENERATED
    public const string CupidNecklaceGold = "npc.cupid_necklace_gold"; // F-130 (the admin: 2,300 Gold)
    public const string ReputationExchange = "npc.reputation_exchange"; // W2 F-46
    public const string BoardLoginLine = "board.login_line"; // the admin 2026-11-08: board above the chat box
    public const string BeginnerGifts = "prize.beginner_gifts"; // W1 prize claims
    public const string OfflinePrize = "prize.offline_prize"; // F-131 offline prize, per hour away
    public const string RookieGrowth = "prize.rookie_growth"; // F-138 newbie growth rewards
    public const string ChatBubbles = "item.chat_bubbles"; // W1 dead items
    public const string CypressLeave = "item.cypress_leave"; // W1 dead items
    public const string HermesHood = "item.hermes_hood"; // ITEMS-HERMES F-56
    public const string PetRename = "item.pet_rename"; // ITEMS-HERMES F-55 (pet details Rename + Renaming Stone)
    public const string Autoloot = "item.autoloot"; // W3 Plump Wheat / Golden Leaf
    public const string TreasureMapDigs = "item.treasure_map_digs"; // Treasure Map dig spots (4470 / 4471)
    public const string MagicBell = "pet.magic_bell_dispatch"; // W3 Magic Bell (F-136)
    public const string MagicBellPrice = "pet.magic_bell_price"; // W3 Magic Bell (F-136), GENERATED 50,000 silver
    public const string MagicBellMerchant = "pet.magic_bell_merchant"; // F-143: Pet Merchant silver listing, default 0
    public const string MagicBellDrops = "pet.magic_bell_drops"; // F-143: 0.5% bell drops from 15 bosses/elites
    public const string MagicBellDropChance = "pet.magic_bell_drop_chance_bp"; // F-143: the admin 0.5% = 50 bp
    public const string MagicBellSlowestMinutes = "pet.dispatch_slowest_minutes"; // W3 Magic Bell, the admin F-136: 70
    public const string MagicBellFastestMinutes = "pet.dispatch_fastest_minutes"; // W3 Magic Bell, the admin F-136: 10
    public const string MagicBellFastestRank = "pet.dispatch_fastest_rank"; // W3 Magic Bell, the admin F-136: rank 75
    public const string PetWork = "pet.work"; // PETWORK F-137
    public const string BeginnerGiftsMaxLevel = "prize.beginner_max_level"; // W1 prize claims, GENERATED
    public const string BoardAnnouncements = "board.announcements";
    public const string BoardLotteryHints = "board.lottery_hints";
    public const string BoardServerInfo = "board.server_info"; // the admin 2026-11-10: server info in the top box, not the chat box
    public const string ChatWorldBossLine = "board.world_boss_chat_line"; // the admin 2026-11-10: boss spawns stay in the chat box
    public const string EventChatLines = "board.event_chat_lines"; // the admin 2026-11-10: event openings stay in the chat box
    public const string MermaidDew = "atlantis.mermaid_dew"; // F-122
    public const string MermaidDewItem = "atlantis.mermaid_dew_item"; // F-122, GENERATED
    public const string SkillDots = "combat.skill_dots"; // W2 skill statuses
    public const string SkillDotChance = "combat.skill_dot_chance"; // W2 skill statuses, GENERATED 50
    public const string SkillDotDamagePercent = "combat.skill_dot_damage_percent"; // W2 skill statuses
    public const string SkillStuns = "combat.skill_stuns"; // W2 skill statuses
    public const string SkillDebuffs = "combat.skill_debuffs"; // W2 skill statuses
    public const string PvpSkills = "pvp.skills"; // W3 PvP skills
    public const string SkillHealOverTime = "combat.skill_heal_over_time"; // W2 friendly statuses (Radiance / Grace)
    public const string SkillHealOverTimePercent = "combat.skill_hot_percent"; // W2 friendly statuses
    public const string SkillCleanse = "combat.skill_cleanse"; // W2 friendly statuses (Divine Intervention)
    public const string ThanksgivingDropPercent = "season.thanksgiving_drop_percent"; // W3 seasons, GENERATED
    public const string ThanksgivingCardPercent = "season.thanksgiving_card_percent"; // W3 seasons, GENERATED
    public const string Professions = "profession.crafting"; // PROF slice 1 (F-59)
    public const string SealBoxes = "item.seal_boxes"; // ROUND3 runtime spawns, F-31
    public const string BossScrolls = "item.boss_scrolls"; // ROUND3 runtime spawns, F-31
    public const string Gathering = "profession.gathering"; // PROF slice 2: Romance Island gathering
    public const string TreasureGuardians = "item.treasure_guardians"; // ROUND3 summons
    public const string TotemPoles = "item.totem_poles"; // ROUND3 summons
    public const string SacredSealer = "npc.sacred_sealer"; // ROUND3 summons, F-27
    public const string ChristmasThresholdPercent = "season.christmas_threshold_percent"; // W3 Christmas, GENERATED
    public const string ChristmasAntlerDropPercent = "season.christmas_antler_drop_percent"; // W3 Christmas, GENERATED
    public const string ChristmasAntlerExperience = "season.christmas_antler_exp"; // W3 Christmas, GENERATED
    public const string ProfessionSkillbookShelf = "profession.skillbook_shelf"; // PROF skillbook source
    public const string KelsisContest = "npc.kelsis_contest"; // KELSIS F-15, default 0 (takes the player's dog)
}

internal static class Finalise2Rates
{
    public const string Category = "Item and NPC handlers (finalise2)";

    public static bool On(string key) => ServerRates.Current.Get(key) >= 1m;

    public static int Amount(string key) => (int)ServerRates.Current.Get(key);

    private static ServerRateDefinition Switch(string key, string label, int order, string source, string text) =>
        new(key, Category, label, "0/1", ServerRateKind.Absolute, 1m, 0m, 1m, 1m, order, source, text);

    public const string BoardCategory = "Message board (above the chat box)";

    private static ServerRateDefinition BoardSwitch(string key, string label, int order, string text) =>
        new(key, BoardCategory, label, "0/1", ServerRateKind.Absolute, 1m, 0m, 1m, 1m, order,
            "Game/GameSessionRegistry.Board.cs", text);

    public const string CombatCategory = "Combat: skill statuses";
    public const string SeasonCategory = "Seasonal events (on/off and dates: Events & notices page)";

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        new(Finalise2RateKeys.ThanksgivingDropPercent, SeasonCategory, "Thanksgiving: turkey item drop chance", "%",
            ServerRateKind.Absolute, 8m, 0m, 100m, 1m, 1, "Application/Seasons/SeasonalEvents.cs",
            "While Thanksgiving is open, each monster killed on Marathon or Peloponnese drops a Savory / Tender Turkey Meat " +
            "or a Gift Certificate this often (the client has no turkey monster; GENERATED)."),
        new(Finalise2RateKeys.ThanksgivingCardPercent, SeasonCategory, "Thanksgiving: Greetings Card drop chance", "%",
            ServerRateKind.Absolute, 1m, 0m, 100m, 1m, 2, "Application/Seasons/SeasonalEvents.cs",
            "Chance of a Thanksgiving Greetings Card from the same monsters (GENERATED). A card in the envoy's second slot " +
            "triples the meat reward (100 -> 300 bound Gold)."),
        new(Finalise2RateKeys.ChristmasThresholdPercent, SeasonCategory, "Christmas: reindeer model size", "%",
            ServerRateKind.Absolute, 10m, 1m, 100m, 1m, 3, "Application/Seasons/ChristmasRules.cs",
            "Antlers each faction's Gigantic Reindeer Model needs, as a percent of the client's 5,000 / 10,000 / 20,000 / " +
            "30,000 / 50,000 for levels 1-5. 10 = 500 ... 5,000 (GENERATED, reachable by about ten players in ten days). " +
            "The client's progress page prints its own numbers, so Santa also sends the real ones in one line (board / top box)."),
        new(Finalise2RateKeys.ChristmasAntlerDropPercent, SeasonCategory, "Christmas: Antler drop chance", "%",
            ServerRateKind.Absolute, 50m, 0m, 100m, 1m, 4, "Application/Seasons/SeasonalEvents.cs",
            "While Christmas is open, a Little Deer in Athens or a Vicious Fawn in the Sparta suburbs drops an Antler this " +
            "often (GENERATED: the client will not use Santa Gloves on a Little Deer)."),
        new(Finalise2RateKeys.ChristmasAntlerExperience, SeasonCategory, "Christmas: EXP per Antler turned in", "EXP",
            ServerRateKind.Absolute, 2000m, 0m, 1000000m, 100m, 5, "Application/Seasons/ChristmasRules.cs",
            "EXP for each Antler given to Santa (times exp.events), plus 1 Talent Point per 10 Antlers (GENERATED)."),
        new(Finalise2RateKeys.PvpSkills, CombatCategory, "PvP: attack skills on enemy players", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 0, "Game/GameSessionRegistry.TrainingDummyDamageSkills.cs",
            "1 = instant single-target damage skills hit players you may attack with a basic attack (other faction, " +
            "PvP map, outside safe zones, duels). Area skills, skills with a cast time and stuns on players come later. " +
            "0 = skills cannot target players, as before."),
        new(Finalise2RateKeys.SkillDots, CombatCategory, "Bleeding / Electrocuted / Burned from skills", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 1, "Game/GameSessionRegistry.SkillDots.cs",
            "1 = Blood Claw, Rage Claw, Thunder, Thundercloud and Fire Lotus can leave their stock damage-over-time status " +
            "on monsters (Status.ini damage per tick). 0 = those skills only do their hit, as before."),
        new(Finalise2RateKeys.SkillDotChance, CombatCategory, "Chance a skill hit starts its damage over time", "%",
            ServerRateKind.Absolute, 50m, 0m, 100m, 1m, 2, "Game/GameSessionRegistry.SkillDots.cs",
            "Percent of hits that start the status (the stock rating scale is unknown; 50 = the server's neutral status chance, GENERATED)."),
        new(Finalise2RateKeys.SkillDotDamagePercent, CombatCategory, "Damage over time: damage per tick", "%",
            ServerRateKind.Absolute, 100m, 0m, 500m, 5m, 3, "Game/GameSessionRegistry.SkillDots.cs",
            "100 = the stock Status.ini damage per tick (e.g. Rage Claw 4: 800 every 2 s for 6 s)."),
        new(Finalise2RateKeys.SkillStuns, CombatCategory, "Dizzy / Concussed / Frozen from Steel Rain, Fire Blast and Ice Cone 4/5", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 4, "Game/GameSessionRegistry.SkillDots.cs",
            "1 = Steel Rain 4/5 (Dizzy 0.5 s / 1 s) and Fire Blast 4/5 (Concussed 2 s) can stop the monsters they hit " +
            "(not bosses), using the chance above. 0 = damage only, as before."),
        new(Finalise2RateKeys.SkillDebuffs, CombatCategory, "Weakness / Blinded from Ice Cone and Holy Light", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 5, "Game/GameSessionRegistry.SkillDots.cs",
            "1 = Ice Cone 1-5 makes monsters take 5-10 % more damage for 8-10 s (Weakness; Ice Cone 4/5 also freezes " +
            "for 0.5 / 1 s with the stun switch) and Holy Light 4/5 lowers a monster's Hit Rating by 200 for 5 s (Blinded), " +
            "using the chance above. 0 = damage only, as before."),
        Switch(Finalise2RateKeys.Professions, "Professions: learn, Activity Points, crafting window", 74,
            "Game/GameClientHandler.Professions.cs",
            "1 = the Profession Mentor teaches a profession for a Profession Skillbook, AP accrues online and offline " +
            "(Lifing_AP), recipe items teach blueprints and the client's Manufacture window crafts them (F-59). " +
            "0 = the mentor's old answers and no profession data is sent."),
        Switch(Finalise2RateKeys.Gathering, "Gathering: Romance Island nodes and the gather window", 75,
            "Game/GameClientHandler.Gathering.cs",
            "1 = the capital Event Transporter's '*To Romance Island' (600) moves level 70+ players to map 45, whose " +
            "Mine Veins, Hunting Ranges and Nifty Gardens open the client's gather window: a placed Hoe, Shovel or " +
            "Arrow, 20 AP per attempt, materials into the bag (PROF slice 2). 0 = no island actors, the transporter's " +
            "old answer, and gather requests are refused."),
        new(Finalise2RateKeys.SkillHealOverTime, CombatCategory, "Radiance / Grace heal over time from Area Heal 4/5", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 6, "Game/GameClientHandler.FriendlySkillStatuses.cs",
            "1 = Area Heal 4 (Radiance of Renewal, 1,500 HP/s) and Area Heal 5 (Grace of Vitality, 1,750 HP/s) also heal " +
            "everyone they healed every second for 8 s (Status.ini 1434/1435). The 'bonus per nearby player' in the text " +
            "has no formula and is not applied. Always applies while on (StatusOdds is a rating, GENERATED). 0 = the instant heal only, as before."),
        new(Finalise2RateKeys.SkillHealOverTimePercent, CombatCategory, "Radiance / Grace: HP per second", "%",
            ServerRateKind.Absolute, 100m, 0m, 500m, 5m, 7, "Game/GameClientHandler.FriendlySkillStatuses.cs",
            "100 = the stock Status.ini HP per second (1,500 / 1,750)."),
        new(Finalise2RateKeys.SkillCleanse, CombatCategory, "Divine Intervention from Heal 4/5 (remove debuffs)", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 8, "Game/GameSessionRegistry.FriendlyCleanse.cs",
            "1 = Heal 4/5 also removes the target's debuffs (Status.ini 1439 'Remove Debuff'): timed hostile statuses " +
            "such as PvP stuns, and PvP Bleeding / Electrocuted / Burned. Mounts, buffs and elemental Burn/Chill stay. " +
            "Always applies while on (StatusOdds is a rating, GENERATED). 0 = the heal only, as before."),
        Switch(Finalise2RateKeys.ProfessionSkillbookShelf, "Professions: Scholarship Recipe Vendor sells the Profession Skillbook", 75,
            "Packets/PacketBuilder.ProfessionSkillbookListing.cs",
            "1 = the Scholarship Recipe Vendor in Athens and Sparta (Athens_137 / Sparta_137) sells the Profession Skillbook " +
            "(4268) for 10,000 silver at the end of tab 1 (GENERATED: retail sold it nowhere). Read each time the shelf is " +
            "opened or bought from, no restart. 0 = the captured shelf only, as before."),
        Switch(Finalise2RateKeys.CypressLeave, "Cypress Leave (4140): name shown as Masked Man", 72,
            "State/MaskedNames.cs",
            "1 = using a Cypress Leave shows the player to others as 'Masked Man' for 2 hours (item text). 0 = the item does nothing, as before."),
        Switch(Finalise2RateKeys.HermesHood, "Hermes Hood (4130): green name to other players for 30 minutes", 72,
            "State/HermesHoods.cs",
            "1 = using a Hermes Hood shows the player to every other player in THAT player's own camp (green name, " +
            "'Players will mistake you for their companion', item text) for 30 minutes. Server rules (PvP, guards, " +
            "faction maps) keep the real camp (GENERATED). 0 = the item does nothing; an active hood stops showing at " +
            "the wearer's next re-announce."),
        Switch(Finalise2RateKeys.PetRename, "Pet rename (pet details Rename; Renaming Stone 4112)", 72,
            "Game/GameClientHandler.PetRename.cs",
            "1 = the pet details Rename button works: 1-16 letters or digits (GENERATED), not in the client's Filter.dat. The " +
            "first rename of each pet is free (client Rookie Guide text); later ones use up a Renaming Stone. " +
            "The stock client then shows its own 'You are called [name] from now on' box. 0 = renames are ignored, as before."),
        Switch(Finalise2RateKeys.SealBoxes, "Seal boxes 14034-14037 release a monster beside you", 74,
            "Game/GameClientHandler.SummonItems.cs",
            "1 = right-clicking a Centaur / Ogre / Persian Soldier Seal Box (or a Sealing Stone, random monster) releases " +
            "that monster beside you for 10 minutes; only you can fight it, and its corpse drops 99 / 198 / 297 Exp " +
            "Pills in stacks of 99 (Sealing Stone 99, GENERATED). Level 20+. Ogre and Persian Soldier are GENERATED " +
            "stand-ins where the map has none (F-31). 0 = the items do nothing, as before."),
        Switch(Finalise2RateKeys.BossScrolls, "Ladon / Cyclops / Medusa scrolls 14060-14065 summon the boss", 75,
            "Game/GameClientHandler.SummonItems.cs",
            "1 = a boss scroll used on its own faction's maps (Athens: Suburbs, Marathon, Parnitha; Sparta: Suburbs, " +
            "Peloponnese, Nemea) summons that boss beside you for 30 minutes; anyone may fight it and the killer gets " +
            "30 Exp Pills (GENERATED, no template loot); with npc.sacred_sealer = 1 (default) the boss is the summoner's " +
            "only and drops its Treasure Chest instead. 0 = the scrolls do nothing, as before."),
        Switch(Finalise2RateKeys.TreasureGuardians, "Treasure Map digs wake their guardians", 76,
            "Game/GameClientHandler.TreasureGuardians.cs",
            "1 = after a Treasure Map dig pays out, its client ItemRefreshMonster row appears: one guardian beside you " +
            "on starting maps (10 minutes), or five bosses at their lairs on Derveni, Argolis, Isthmus of Corinth, " +
            "Megara, Plataea or Thermopylae (30 minutes). Only the digger can fight them; kill EXP only, no loot " +
            "(GENERATED). 0 = digs give loot only, as before."),
        Switch(Finalise2RateKeys.TotemPoles, "Summoning Totem Poles 4473-4476 call faction soldiers", 77,
            "Game/GameClientHandler.TotemPoles.cs",
            "1 = a Totem Pole used near enemy players (30 units, GENERATED) summons 5 soldiers or 3 mages of your " +
            "faction around you for 60 seconds (item text). They fight enemy players only; your faction cannot hurt " +
            "them. 0 = the items do nothing, as before."),
        Switch(Finalise2RateKeys.SacredSealer, "Sacred Sealer sells Ladon / Cyclops / Medusa scrolls", 78,
            "Game/GameClientHandler.SacredSealer.cs",
            "1 = the Sacred Sealer (Athens_130 / Sparta_130) sells your faction's Ladon, Cyclops and Medusa Scroll for " +
            "180 / 900 / 1,800 Gold (client text), and a scroll boss is fought by its summoner only and drops its " +
            "Copper / Silver / Gold Treasure Chest. 0 = the page shows its buttons only, and scroll bosses drop " +
            "30 Exp Pills to anyone, as before."),
        Switch(Finalise2RateKeys.Autoloot, "Plump Wheat / Golden Leaf autoloot (4468 / 4469)", 73,
            "Game/GameClientHandler.Autoloot.cs",
            "1 = using a Plump Wheat (4 hours) or Golden Leaf (7 days) switches on the client's autoloot (AFK window) and " +
            "the remaining time is sent again at every login. 0 = the items do nothing, as before."),
        Switch(Finalise2RateKeys.TreasureMapDigs, "Treasure Map digs at the marked spot (4470 / 4471)", 74,
            "Game/GameClientHandler.TreasureMapDigs.cs",
            "1 = a Treasure Map is used at the spot its tooltip shows (ItemDynamicAttribute.xml, within 10 units; the " +
            "client only allows the right-click there) and gives one treasure from the Random Quest loot table " +
            "(GENERATED). No guardian monsters yet: the server cannot add monsters to an open-world map at runtime. " +
            "0 = the old Random Quest rule (anywhere outside the capitals, only while the Random Quest event is on). " +
            "Maps always carry their spot either way."),
        // W3 Magic Bell (F-136, the admin 2026-11-09: "anywhere from 10 mins to 70 mins depending on the pet's stats,
        // rank 75 should see the lower bound").
        Switch(Finalise2RateKeys.MagicBell, "Magic Bell: Send Pet on a daily quest (4113)", 74,
            "Game/GameClientHandler.PetDispatch.cs",
            "1 = the Quest Log's Send Pet button works: a summoned pet with the Quest Dispatch talent takes one carried " +
            "daily quest and one Magic Bell, and finishes it after 10-70 minutes (by pet rank) for the quest's normal reward; " +
            "the pet stays summoned and usable. Bells come from the Item Mall (pets tab, 30 Gold) and 0.5% boss/elite " +
            "drops (pet.magic_bell_drops). 0 = the button is refused, and no bells at the Pet Merchants either."),
        // F-143 (the admin 2026-11-09: "30g in mall or in some loot tables 0.5%"): the captured mall already lists the bell
        // at 30 Gold, so the GENERATED 50,000-silver Pet Merchant listing is off by default (a new key, so a database
        // that stored the old price still loses the listing).
        new(Finalise2RateKeys.MagicBellMerchant, Category, "Magic Bell: also sold by the Pet Merchants (silver)", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 75, "Packets/PacketBuilder.MagicBellListing.cs",
            "1 = the Athens and Sparta Pet Merchants also list the Magic Bell (tab 1) for the silver price below " +
            "(needs pet.magic_bell_dispatch = 1). 0 (default, the admin F-143) = the bell comes from the Item Mall at 30 Gold " +
            "and from boss/elite drops only."),
        new(Finalise2RateKeys.MagicBellPrice, Category, "Magic Bell: Pet Merchant price", "Silver", ServerRateKind.Absolute,
            50_000m, 1m, 10_000_000m, 1m, 75, "Packets/PacketBuilder.MagicBellListing.cs",
            "Silver price of one Magic Bell at the Pet Merchants while pet.magic_bell_merchant = 1 (GENERATED)."),
        Switch(Finalise2RateKeys.MagicBellDrops, "Magic Bell: drops from 15 bosses and elites", 75,
            "Application/World/Content/MagicBellLootRule.cs",
            "1 = a kill of one of 15 named Lv47-174 bosses and elites (list in the code, GENERATED) also drops one Magic " +
            "Bell (4113) at the chance below, on top of and outside its loot table (drop.chance and drop.quantity do " +
            "not apply). No SQL: their tables are full. 0 = no bell drops. Read at each kill."),
        new(Finalise2RateKeys.MagicBellDropChance, Category, "Magic Bell: drop chance per kill", "basis points",
            ServerRateKind.Absolute, 50m, 0m, 10_000m, 1m, 75, "Application/World/Content/MagicBellLootRule.cs",
            "Chance per kill of a listed boss/elite, in 1/10,000 (the admin F-143: 0.5% = 50)."),
        new(Finalise2RateKeys.MagicBellSlowestMinutes, Category, "Magic Bell: slowest dispatch (rank 0)", "minutes",
            ServerRateKind.Absolute, 70m, 1m, 1440m, 1m, 76, "State/PetQuestDispatchPolicy.cs",
            "How long a rank-0 pet takes to finish a dispatched quest (the admin F-136: 70)."),
        new(Finalise2RateKeys.MagicBellFastestMinutes, Category, "Magic Bell: fastest dispatch", "minutes",
            ServerRateKind.Absolute, 10m, 1m, 1440m, 1m, 77, "State/PetQuestDispatchPolicy.cs",
            "How long a pet at or above the fastest rank takes (the admin F-136: 10). Ranks in between are linear."),
        new(Finalise2RateKeys.MagicBellFastestRank, Category, "Magic Bell: rank that reaches the fastest time", "rank",
            ServerRateKind.Absolute, 75m, 1m, 655m, 1m, 78, "State/PetQuestDispatchPolicy.cs",
            "Pet rank at which a dispatch takes the fastest time (the admin F-136: rank 75)."),
        Switch(Finalise2RateKeys.PetWork, "Pet work (the pet window's Work button)", 74,
            "Game/GameClientHandler.PetWork.cs",
            "1 = a summoned pet with the Work talent can be sent to work for 1/3/8/24 hours; the player pays the " +
            "Pet_Job.xml food cost for their level in Silver or Gold and gets its EXP and Talent Points when the pet " +
            "returns (20% if stopped early, F-137). 0 = the Work button does nothing; pets already working still " +
            "come back."),
        Switch(Finalise2RateKeys.ChatBubbles, "Chat Bubble 1 / 2 items (4110 / 4111)", 71,
            "Game/GameClientHandler.ChatBubbles.cs",
            "1 = using a Chat Bubble gives your chat lines that bubble style for 3 hours (red for Chat Bubble 1; this client draws Chat Bubble 2 as the normal bubble). 0 = the items do nothing, as before."),
        Switch(Finalise2RateKeys.BeginnerGifts, "Online-time beginner gifts (the gift button)", 69,
            "Game/GameClientHandler.TimePrize.cs",
            "1 = the client's gift window counts online time and pays the 8 beginner gifts of its Timeprize.xml (5 to 700 " +
            "minutes online in total; items bound). 0 = the window stays empty, as before."),
        Switch(Finalise2RateKeys.OfflinePrize, "Offline prize: EXP / TP / energy per hour away (Lv60+, 24 h+)", 74,
            "Game/GameClientHandler.OfflinePrize.cs",
            "1 = a level 60+ character back after 24 hours or more gets its client Timeprize.xml LostPrize gains for each " +
            "whole hour away (Lv60: 30 EXP, 0.021 TP and 0.021 Stored Energy an hour; no cap; EXP and TP times the event " +
            "rate exp.events), shown in the client's 'Over 24h to your last logout' box. 0 = nothing, as before."),
        Switch(Finalise2RateKeys.RookieGrowth, "Newbie growth rewards (Rookie Guide button by the minimap)", 74,
            "Game/GameClientHandler.RookieGrowth.cs",
            "1 = the Rookie Guide window lists the growth entries this server can check (quest, map and AFK tips, " +
            "zodiac, reputation, weapon/armour rank, level 20-120) and Complete pays the client's RookieQuestReward.xml " +
            "reward once per character. 0 = the window stays empty, as before."),
        new(Finalise2RateKeys.BeginnerGiftsMaxLevel, Category, "Beginner gifts: highest level that earns online time", "level",
            ServerRateKind.Absolute, 200m, 1m, 200m, 1m, 70, "Game/GameClientHandler.TimePrize.cs",
            "Characters above this level neither earn beginner-gift time nor get the window. 200 = everyone (GENERATED)."),
        BoardSwitch(Finalise2RateKeys.BoardLoginLine, "Board: online line at login", 1,
            "1 = every player gets 'Online: N | Athenians | Spartans' as a game log line when they enter the world; on " +
            "client 1.4.3 it also switches the board on (keep it on: the server welcome and board lines need it). The repeat interval is on the editor's Events & notices page (population line)."),
        BoardSwitch(Finalise2RateKeys.BoardAnnouncements, "Board: show GM announcements", 2,
            "1 = a GM /announce (and the editor's announce) also appears on the board above the chat box."),
        BoardSwitch(Finalise2RateKeys.BoardLotteryHints, "Board: show lottery hints", 3,
            "1 = the Rich Man Envoy's 'lowest unique number' hints also appear on the board."),
        BoardSwitch(Finalise2RateKeys.BoardServerInfo, "Server messages go to the top box, not the chat box", 4,
            "1 = the chat box below is for player chat: the online-players line is a message line inside the board, " +
            "and event-NPC notices, the Lunar Beast line and GM announcements go to the board (top box) instead of " +
            "World chat. Long lines wrap over up to 3 board lines. Clients without the board get them as game log lines. " +
            "0 = the old routing (World chat lines; online line on board line 1)."),
        BoardSwitch(Finalise2RateKeys.ChatWorldBossLine, "World boss spawns also in the chat box", 5,
            "1 = a world boss respawn is also written as a World chat line (to the factions the centre notice reaches), " +
            "so it stays in the chat history. Needs event.world_boss_notice = 1. 0 = centre notice only, as before."),
        BoardSwitch(Finalise2RateKeys.EventChatLines, "Event openings also in the chat box", 6,
            "1 = compiled event-opening notices (today: Hera's Labyrinth 'One minute to ...') are also written as a World " +
            "chat line. Your own 'event transporters now open' notices: Events & notices page, delivery 'Chat box line'. " +
            "0 = centre notice only, as before."),
        Switch(Godswar.Server.Application.Mentor.MentorPolicy.MastersEthicsRateKey,
            "Mentor: Masters Ethics instead of per-level B-Gold", 68, "Application/Mentor/MentorPolicy.cs",
            "1 = each level an apprentice gains gives the master 10 x that level in Masters Ethics (GENERATED), traded at " +
            "the Exchange Mentor once a month: 8,000 / 25,000 / 70,000 -> 1,380 / 2,300 / 3,220 B-Gold (client texts, " +
            "F-23). 0 = the Stage 3 rule: 20 B-Gold per apprentice level. Graduation rewards are unchanged."),
        Switch(Finalise2RateKeys.ReputationExchange, "Exchange Mentor: Reputation for B-Gold (monthly)", 67,
            "Game/GameClientHandler.ReputationExchange.cs",
            "1 = the Exchange Mentor / Alcippe trades Reputation (honour) for B-Gold once per month: 1,200 -> 920, " +
            "5,000 -> 1,600, 15,000 -> 2,300 (client texts, F-46). Masters Ethics answers 'not enough points' until it " +
            "exists. 0 = the confirmations do nothing, as before."),
        Switch(Finalise2RateKeys.MermaidDew, "Atlantis Mermaid: high HP, net capture gives a Morning Dew", 65,
            "Application/WorldInstances/AtlantisPetSpawnPolicy.cs",
            "1 = the Atlantis Mermaid has elite health (no ten-hit rule) and a Mysterious Tuck Net capture gives the " +
            "Morning Dew below instead of a Merman egg (the admin F-122). The Siren still gives the Merman egg. Read when an " +
            "Atlantis run starts. 0 = the old ten-hit Mermaid with a Merman egg."),
        new(Finalise2RateKeys.MermaidDewItem, Category, "Atlantis Mermaid: Morning Dew item id", "item",
            ServerRateKind.Absolute, 10132m, 10130m, 10134m, 1m, 66, "Application/WorldInstances/AtlantisPetSpawnPolicy.cs",
            "Which Morning Dew (10130-10134 = Morning Dew 1-5) a Mermaid capture gives. 10132 (Morning Dew 3) is GENERATED."),
        Switch(Finalise2RateKeys.PersonalHelper, "Personal Helper: batch item use", 64,
            "Game/GameClientHandler.PersonalHelper.cs",
            "1 = the Personal Helper uses a placed Morning Dew / EXP Stone / Talent Stone / Gold Bag / B-Gold Bag 1-99 " +
            "times in one go (client text NF_L0_BATCH100T). 0 = page only."),
        // KELSIS (F-15): OFF by default. A submitted dog is taken from the player (snapshotted first; restore SQL in
        // server/docs/kelsis-contest/), so turning it on is the admin's call.
        new(Finalise2RateKeys.KelsisContest, Category, "Kelsis lowest-stat dog contest (Lelantine Farm)", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 67, "Game/GameClientHandler.KelsisContest.cs",
            "1 = Delighted Kelsis takes a summoned Loyal Dog whose round stat (Rank/Agility/.../Luck) is lower than " +
            "every dog given this round, for 1 Morning Dew; the lowest dog's owner claims 2,300 B-Gold at the " +
            "Battlefield Awarder (needs battlefield.awarder = 1). The dog is gone from the player. 0 = no dialogue."),
        Switch(Finalise2RateKeys.HolyStoneRefund, "Holy Stone Redeemer: details, exchange and refund pages", 55,
            "Game/GameClientHandler.HolyStoneRedeemerPages.cs",
            "1 = the redeemer offers its three client pages: details (information only), exchange (a Holy Stone Package " +
            "Voucher placed in the slot becomes a Holy Stone Package; needs the voucher switch) and refund (a voucher in " +
            "the slot is taken back for 1,800 Gold, client text HolyPackage10 and the Item Mall price). 0 = the old single " +
            "details page, which exchanges carried vouchers when opened."),
        Switch(Finalise2RateKeys.Lottery, "Rich Man Envoy lottery (Rags to Riches)", 62,
            "Game/GameClientHandler.Lottery.cs",
            "1 = 19:00-23:00: 10 Level 1 Crystals give an Exp Pill and a number 1-10,000; the lowest unique number of " +
            "each 100 entries wins (client text Lotto5). 0 = pages only."),
        new(Finalise2RateKeys.CupidNecklaceGold, Category, "Cupid sells a Whelk Necklace for (Gold)", "Gold", ServerRateKind.Absolute,
            2300m, 0m, 100000m, 1m, 64, "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "the admin F-130: Cupid's 'Buy a Whelk Necklace' entry (needs client 1.4.5) charges this much Gold. 0 = Cupid does not sell it; Pan still gives one free."),
        new(Finalise2RateKeys.LotteryPrizeGold, Category, "Lottery prize (Gold)", "Gold", ServerRateKind.Absolute,
            500m, 0m, 100000m, 1m, 63, "Game/GameClientHandler.Lottery.cs",
            "Gold paid to each round's winner. The client does not state the prize: GENERATED."),
        Switch(Finalise2RateKeys.Apothecary, "Apothecary: gems to EXP potions, potion transfer", 61,
            "Game/GameClientHandler.Apothecary.cs",
            "1 = the Apothecary exchanges Lv1/Lv2 gems and Lv1 Crystals for pet/EXP potions (daily limits) and transfers " +
            "8 potions into 1 of the next tier, as its client text says (F-27). 0 = pages only."),
        Switch(Finalise2RateKeys.MountFeeder, "Mount Feeder: upgrade, degrade, re-skin, shifter, bind gear", 60,
            "Game/GameClientHandler.MountFeeder.cs",
            "1 = the Mount Feeder upgrades mounts with Soul Stones, degrades them (stones refunded), re-skins them with a " +
            "Mount Shifter, exchanges Lv40 mounts for shifters and binds mount gear (F-52). 0 = pages only, as before."),
        // W0 F-111: gold-box dial contents. 0 = today's contents, 1 = original retail dial pools, 2 = combined.
        new(Godswar.Server.State.GiftPackDialPools.RateKey, Category, "Gold-box dial contents (0 today, 1 retail, 2 both)",
            "mode", ServerRateKind.Absolute, 0m, 0m, 2m, 1m, 4, "State/GiftPackDialPools.cs",
            "0 = the current box contents. 1 = the original retail dial.ini pools (with their fixed bonus stats). " +
            "2 = both combined, even odds between today's list and the retail pool. Which pool fits which box is " +
            "inferred from item levels (GENERATED)."),
        // FINALISE2b (F-121): OFF by default. Turn on only after client 1.4.2 (Quest.xml MaxLevel 200) is published:
        // with it on, an npc that gives one of these quests opens its quest window for Lv141-200 players.
        new(Finalise2RateKeys.QuestCapsTo200, Category, "Quests capped 121-140 open to Lv200", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 5, "Domain/World/Content/QuestLevelCapRaise.Generated.cs",
            "1 = the 159 quests whose cap was 121-140 can be taken up to Lv200 (the admin F-121). Needs client 1.4.2 " +
            "first. 0 = the old nine-level band."),
        // ROUND3 F-123: OFF by default. Turn on only after client 1.4.6 (Quest.xml MaxLevel 200 for these ids) is published.
        // The 196 are main story: the free ones were already open from MinLevel up; with it on the 92 exact-level Thebes
        // dailies open from MinLevel to Lv200 too (one a day each, rewards as authored).
        new(Finalise2RateKeys.QuestCaps120To200, Category, "Quests capped 120 open to Lv200", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 5, "Domain/World/Content/QuestLevelCapRaise120.Generated.cs",
            "1 = the 196 main-story quests whose cap was 120 can be taken up to Lv200 (the admin F-123); the exact-level " +
            "Thebes dailies among them open from their level to 200; scroll quests 646/1646 \"[Lv131] Summons (4)\" " +
            "open Lv126-200 instead of Lv126-140 (the admin F-70). Needs client 1.4.6 first. 0 = as before."),
        Switch(Finalise2RateKeys.BGoldTrader, "B-Gold Trader daily exchange", 10,
            "Game/GameClientHandler.Finalise2Npc.cs",
            "1 = the B-Gold Trader takes 180 Gold once a day (00:00-23:55 realm time) for the B-Gold below (F-28). " +
            "0 = description only, as before."),
        new(Finalise2RateKeys.BGoldTraderAmount, Category, "B-Gold Trader: B-Gold paid", "B-Gold",
            ServerRateKind.Absolute, 360m, 0m, 100000m, 1m, 11, "Game/GameClientHandler.Finalise2Npc.cs",
            "B-Gold for 180 Gold. the admin chose 360 (NPC description); the client's success line says 900, so the " +
            "server also sends a chat line with the real amount."),
        Switch(Finalise2RateKeys.MysteryMerchant, "Mystery Merchant daily goods", 20,
            "Game/GameClientHandler.Finalise2Npc.cs",
            "1 = Spring Water (Restricted) 21,000 Gold once a day and Morning Dew 2 (Restricted) 200 Gold +100 per " +
            "buy, 30 a day (client text SecretShop_01, F-72). 0 = the old fixed line, nothing sold."),
        Switch(Finalise2RateKeys.CadianPlates, "Cadian Generals take Cadian Plates", 30,
            "Game/GameClientHandler.Finalise2Npc.cs",
            "1 = General Soterios / Lykaios (Marathon / Peloponnese) take 1-99 Cadian Plates for EXP (F-48). " +
            "0 = description only."),
        new(Finalise2RateKeys.CadianPlateExperience, Category, "Cadian Plate EXP each", "EXP",
            ServerRateKind.Absolute, 1000m, 0m, 1000000m, 1m, 31, "Game/GameClientHandler.Finalise2Npc.cs",
            "EXP per plate (client text NF_L0_JPT100: 1,000). Paid as is, not multiplied by exp.events."),
        Switch(Finalise2RateKeys.WarSupplierBuyBack, "War Supplier buys back war materials", 40,
            "Game/GameClientHandler.Finalise2Npc.cs",
            "1 = opening a War Supplier hands in carried Food/Weapon/Medicine/Wine, 6 a day (godswar.online node " +
            "110); rewards per resource point are GENERATED (F-53). 0 = page only."),
        Switch(Finalise2RateKeys.Vouchers, "Holy Box vouchers and Sacrificial Bag", 50,
            "Game/GameClientHandler.Finalise2Items.cs",
            "1 = right-clicking Holy Box II/III Voucher gives a full Holy Box II/III, and the Sacrificial Bag " +
            "donates 500,000 Silver to the player's guild (F-90). 0 = nothing happens, as before."),
        Switch(Finalise2RateKeys.FactionChangeTeleport, "Faction change: teleport instead of logout", 60,
            "Game/ScriptedNpcDialogueCatalog.IdentityChange.cs",
            "1 = after a faction change the player is moved to the new faction's capital at once (F-83). " +
            "0 = the old logout; the player logs in at the new capital."),
        Switch(Finalise2RateKeys.GenderChangeTeleport, "Gender change: re-place on the spot instead of logout", 61,
            "Game/ScriptedNpcDialogueCatalog.IdentityChange.cs",
            "1 = after a gender change the character is reloaded and re-placed on the same spot (a same-map scene " +
            "change), so the player and everyone around see the new model at once (F-83). 0 = the old logout."),
        Switch(Finalise2RateKeys.TalentReset, "Zeus' Baptism resets talents", 70,
            "Game/GameClientHandler.Finalise2Items.cs",
            "1 = Zeus' Baptism refunds every spent talent point and clears the talent ranks (F-54). 0 = nothing."),
        Switch(Finalise2RateKeys.PetItems, "Pet EXP stones and pet water compound", 80,
            "State/PetExperienceItemPolicy.cs, State/ForgeCompoundPlanner.cs",
            "1 = Pet EXP Stones 10147-10149 give the carried pet EXP like Morning Dew (F-93) and the Gems tab compounds " +
            "6 Spring Water into 1 Juice of Rebirth (BijouForge types 8/9, F-109). 0 = neither."),
        Switch(Finalise2RateKeys.HairItems, "Hairdye and Pretty Wig", 90,
            "Game/GameClientHandler.Finalise2Items.cs",
            "1 = Hairdye:Silver sets hair colour 4 (the silver textures) and Pretty Wig moves to the next hair style " +
            "(GENERATED order), then the player is re-placed on the spot so the new hair is drawn (F-56). 0 = nothing.")
    ];
}
