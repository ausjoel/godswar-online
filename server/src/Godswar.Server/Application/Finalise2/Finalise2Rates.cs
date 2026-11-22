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
    public const string LoginAwards = "prize.login_awards"; // W4-B login-window awards
    public const string QueuedBuffs = "prize.queued_buffs"; // W4-B buffs queued for an offline character
    public const string PrizeChest = "prize.prize_chest"; // W4-B Prize Chest (Athens_123 / Sparta_123) award storage
    public const string PrizeChestWindow = "prize.prize_chest_window"; // W4-B native award tab (on since W5-F)
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
    public const string SkillBookChestDrops = "drop.skill_book_chests"; // B22 K1 (d43): world-boss skill-book chests
    public const string SkillBookChestChance = "drop.skill_book_chest_chance_bp"; // B22 K1 (d43): the admin 5% = 500 bp
    public const string GodGemDrops = "gem.god_gem_drops"; // B22 G1 d38: world-boss god gems, default off
    public const string GodGemDropChance = "gem.god_gem_drop_chance_bp"; // B22 G1 d38: about 2% = 200 bp
    public const string MedalDrops = "medal.drops"; // B22 G2 d36: Valour Laurel / Aether Crest drops, default on
    public const string MedalLaurelEliteChance = "medal.laurel_elite_chance_bp"; // B22 G2: 2500 = 25%
    public const string MedalLaurelBossCount = "medal.laurel_boss_count"; // B22 G2: 5 per boss kill
    public const string MedalCrestNormalChance = "medal.crest_normal_chance_bp"; // B22 G2: 1000 = 10%
    public const string MedalCrestBossCount = "medal.crest_boss_count"; // B22 G2: 5 per boss kill
    public const string SanctumGemDrops = "gem.sanctum_drops"; // B22 G2 d38: Radiant/Primal gems from the Aether Sanctum
    public const string RadiantNormalChance = "gem.radiant_normal_chance_bp"; // B22 G2: 100 = 1%
    public const string RadiantEliteChance = "gem.radiant_elite_chance_bp"; // B22 G2: 2500 = 25%
    public const string PrimalEliteChance = "gem.primal_elite_chance_bp"; // B22 G2: 500 = 5%
    public const string PrimalBossCount = "gem.primal_boss_count"; // B22 G2: 1 per boss kill
    public const string MagicBellSlowestMinutes = "pet.dispatch_slowest_minutes"; // W3 Magic Bell, the admin F-136: 70
    public const string MagicBellFastestMinutes = "pet.dispatch_fastest_minutes"; // W3 Magic Bell, the admin F-136: 10
    public const string MagicBellFastestRank = "pet.dispatch_fastest_rank"; // W3 Magic Bell, the admin F-136: rank 75
    public const string PetWork = "pet.work"; // PETWORK F-137
    public const string BeginnerGiftsMaxLevel = "prize.beginner_max_level"; // W1 prize claims, GENERATED
    public const string BoardAnnouncements = "board.announcements";
    public const string BoardLotteryHints = "board.lottery_hints";
    public const string BoardMessageTime = "board.message_time"; // B21 item 19: HH:MM in front of each board message
    public const string BoardServerInfo = "board.server_info"; // the admin 2026-11-10: server info in the top box, not the chat box
    public const string ChatWorldBossLine = "board.world_boss_chat_line"; // the admin 2026-11-10: boss spawns stay in the chat box
    public const string EventChatLines = "board.event_chat_lines"; // the admin 2026-11-10: event openings stay in the chat box
    public const string EventOpenNotices = "board.event_open_notices"; // W4F: client SysMsg event-opening lines
    public const string MermaidDew = "atlantis.mermaid_dew"; // F-122
    public const string MermaidDewItem = "atlantis.mermaid_dew_item"; // F-122, GENERATED
    public const string MermaidKillExpStone = "atlantis.mermaid_kill_exp_stone"; // the admin 2026-11-11
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
    public const string AccountBonusIcons = "status.account_bonus_icons"; // W6-B: donator / battle pass / area icons, default 0
}

internal static class Finalise2Rates
{
    public const string Category = "Items and NPC features";

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
            "Chance that a monster killed on Marathon or Peloponnese drops a Savory or Tender " +
            "Turkey Meat or a Gift Certificate while Thanksgiving is open. Our own value: the " +
            "original is unknown."),
        new(Finalise2RateKeys.ThanksgivingCardPercent, SeasonCategory, "Thanksgiving: Greetings Card drop chance", "%",
            ServerRateKind.Absolute, 1m, 0m, 100m, 1m, 2, "Application/Seasons/SeasonalEvents.cs",
            "Chance that the same monsters drop a Thanksgiving Greetings Card. A card in the " +
            "envoy's second slot triples the meat reward (100 becomes 300 bound Gold). Our own " +
            "value: the original is unknown."),
        new(Finalise2RateKeys.ChristmasThresholdPercent, SeasonCategory, "Christmas: reindeer model size", "%",
            ServerRateKind.Absolute, 10m, 1m, 100m, 1m, 3, "Application/Seasons/ChristmasRules.cs",
            "How many Antlers each faction's Gigantic Reindeer Model needs, as a percent of the " +
            "game's own numbers (5,000 to 50,000 for levels 1 to 5). 10 = 500 to 5,000, which " +
            "about ten players can reach in ten days. The game's progress page shows its own " +
            "numbers, so Santa also sends the real ones."),
        new(Finalise2RateKeys.ChristmasAntlerDropPercent, SeasonCategory, "Christmas: Antler drop chance", "%",
            ServerRateKind.Absolute, 50m, 0m, 100m, 1m, 4, "Application/Seasons/SeasonalEvents.cs",
            "Chance that a Little Deer in Athens or a Vicious Fawn in the Sparta suburbs drops an " +
            "Antler while Christmas is open. Our own value: the original is unknown."),
        new(Finalise2RateKeys.ChristmasAntlerExperience, SeasonCategory, "Christmas: EXP per Antler turned in", "EXP",
            ServerRateKind.Absolute, 2000m, 0m, 1000000m, 100m, 5, "Application/Seasons/ChristmasRules.cs",
            "EXP for each Antler given to Santa. Every 10 Antlers also give 1 Talent Point. The " +
            "'Event NPC EXP & Talent' setting multiplies it."),
        new(Finalise2RateKeys.PvpSkills, CombatCategory, "PvP: attack skills on enemy players", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 0, "Game/GameSessionRegistry.TrainingDummyDamageSkills.cs",
            "Lets attack skills hit enemy players. On: instant single-target damage skills work " +
            "on any player you may attack with a normal hit (other faction, PvP map, outside safe " +
            "zones, duels). Off: skills cannot target players. Area skills, skills with a cast " +
            "time and stuns on players are not built yet."),
        new(Finalise2RateKeys.SkillDots, CombatCategory, "Bleeding / Electrocuted / Burned from skills", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 1, "Game/GameSessionRegistry.SkillDots.cs",
            "Lets skills leave damage over time on monsters. On: Blood Claw, Rage Claw, Thunder, " +
            "Thundercloud and Fire Lotus can cause Bleeding, Electrocuted or Burned. Off: those " +
            "skills only do their hit."),
        new(Finalise2RateKeys.SkillDotChance, CombatCategory, "Chance a skill hit starts its damage over time", "%",
            ServerRateKind.Absolute, 50m, 0m, 100m, 1m, 2, "Game/GameSessionRegistry.SkillDots.cs",
            "Percent of skill hits that start a status such as Bleeding, a stun or Weakness. 50 " +
            "is the server's neutral chance. The original game's chance is unknown, so this is " +
            "our own value."),
        new(Finalise2RateKeys.SkillDotDamagePercent, CombatCategory, "Damage over time: damage per tick", "%",
            ServerRateKind.Absolute, 100m, 0m, 500m, 5m, 3, "Game/GameSessionRegistry.SkillDots.cs",
            "Size of each damage-over-time tick, as a percent of the game's own numbers. 100 = " +
            "normal (for example Rage Claw 4: 800 every 2 seconds for 6 seconds)."),
        new(Finalise2RateKeys.SkillStuns, CombatCategory, "Dizzy / Concussed / Frozen from Steel Rain, Fire Blast and Ice Cone 4/5", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 4, "Game/GameSessionRegistry.SkillDots.cs",
            "Lets skills stun monsters. On: Steel Rain 4 and 5 (Dizzy, 0.5 or 1 second) and Fire " +
            "Blast 4 and 5 (Concussed, 2 seconds) can stop the monsters they hit, at the status " +
            "chance above. Off: damage only. Bosses are never stunned."),
        new(Finalise2RateKeys.SkillDebuffs, CombatCategory, "Weakness / Blinded from Ice Cone and Holy Light", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 5, "Game/GameSessionRegistry.SkillDots.cs",
            "Lets skills weaken monsters. On: Ice Cone makes monsters take 5-10% more damage for " +
            "8-10 seconds (Weakness) and Holy Light 4 and 5 lowers their Hit Rating by 200 for 5 " +
            "seconds (Blinded), at the status chance above. Off: damage only. Ice Cone 4 and 5 " +
            "also freeze briefly when skill stuns are on."),
        Switch(Finalise2RateKeys.Professions, "Professions: learn, Activity Points, crafting window", 74,
            "Game/GameClientHandler.Professions.cs",
            "Runs professions and crafting. On: the Profession Mentor teaches a profession for a " +
            "Profession Skillbook, Activity Points build up over time, recipe items teach " +
            "blueprints and the Manufacture window crafts them. Off: the mentor gives his old " +
            "answers and the game shows no profession."),
        Switch(Finalise2RateKeys.Gathering, "Gathering: Romance Island nodes and the gather window", 75,
            "Game/GameClientHandler.Gathering.cs",
            "Runs gathering on Romance Island. On: the Event Transporter takes players of level " +
            "70 or more to the island, where Mine Veins, Hunting Ranges and Nifty Gardens can be " +
            "gathered with a Hoe, Shovel or Arrow for 20 Activity Points a try. Off: the island " +
            "is empty and gathering is refused."),
        new(Finalise2RateKeys.SkillHealOverTime, CombatCategory, "Radiance / Grace heal over time from Area Heal 4/5", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 6, "Game/GameClientHandler.FriendlySkillStatuses.cs",
            "Adds the heal over time of Area Heal 4 and 5. On: everyone they heal is also healed " +
            "every second for 8 seconds (1,500 or 1,750 HP a second). Off: the instant heal only. " +
            "The 'bonus per nearby player' in the skill text is not applied."),
        new(Finalise2RateKeys.SkillHealOverTimePercent, CombatCategory, "Radiance / Grace: HP per second", "%",
            ServerRateKind.Absolute, 100m, 0m, 500m, 5m, 7, "Game/GameClientHandler.FriendlySkillStatuses.cs",
            "Size of the Area Heal 4 and 5 heal over time, as a percent of the game's own " +
            "numbers. 100 = normal (1,500 or 1,750 HP a second)."),
        new(Finalise2RateKeys.SkillCleanse, CombatCategory, "Divine Intervention from Heal 4/5 (remove debuffs)", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 8, "Game/GameSessionRegistry.FriendlyCleanse.cs",
            "Lets Heal 4 and 5 remove debuffs. On: the heal also clears harmful timed statuses " +
            "from the target, such as PvP stuns, Bleeding, Electrocuted and Burned. Off: the heal " +
            "only. Mounts, buffs and elemental Burn or Chill always stay."),
        Switch(Finalise2RateKeys.ProfessionSkillbookShelf, "Professions: Scholarship Recipe Vendor sells the Profession Skillbook", 75,
            "Packets/PacketBuilder.ProfessionSkillbookListing.cs",
            "Lets the Scholarship Recipe Vendor in both capitals sell the Profession Skillbook. " +
            "On: he sells it for 10,000 Silver. Off: his normal goods only. The original game " +
            "sold the book nowhere, so this is our own addition."),
        Switch(Finalise2RateKeys.CypressLeave, "Cypress Leave (4140): name shown as Masked Man", 72,
            "State/MaskedNames.cs",
            "Makes the Cypress Leave work. On: using one shows the player to others as 'Masked " +
            "Man' for 2 hours. Off: the item does nothing."),
        Switch(Finalise2RateKeys.HermesHood, "Hermes Hood (4130): green name to other players for 30 minutes", 72,
            "State/HermesHoods.cs",
            "Makes the Hermes Hood work. On: for 30 minutes every other player sees the wearer " +
            "with a green name, as if in their own faction. Off: the item does nothing. Only the " +
            "name colour changes: PvP, guards and faction maps still use the real faction."),
        Switch(Finalise2RateKeys.PetRename, "Pet rename (pet details Rename; Renaming Stone 4112)", 72,
            "Game/GameClientHandler.PetRename.cs",
            "Makes the pet Rename button work. On: a pet can be renamed to 1 to 16 letters or " +
            "digits; the first rename of each pet is free, later ones use a Renaming Stone. Off: " +
            "renames are ignored. Words on the game's blocked list are refused."),
        Switch(Finalise2RateKeys.SealBoxes, "Seal boxes 14034-14037 release a monster beside you", 74,
            "Game/GameClientHandler.SummonItems.cs",
            "Makes the Seal Boxes and Sealing Stone release a monster. On: the monster appears " +
            "beside the player for 10 minutes, only that player can fight it, and it drops 99 to " +
            "297 Exp Pills. Off: the items do nothing. Level 20 or more."),
        Switch(Finalise2RateKeys.BossScrolls, "Ladon / Cyclops / Medusa scrolls 14060-14065 summon the boss", 75,
            "Game/GameClientHandler.SummonItems.cs",
            "Makes the Ladon, Cyclops and Medusa scrolls summon their boss. On: used on the " +
            "player's own faction maps, a scroll summons the boss beside the player for 30 " +
            "minutes. Off: the scrolls do nothing. While 'Sacred Sealer sells Ladon / Cyclops / " +
            "Medusa scrolls' is on, only the summoner can fight the boss and it drops its " +
            "Treasure Chest; otherwise anyone can, and the killer gets 30 Exp Pills."),
        Switch(Finalise2RateKeys.TreasureGuardians, "Treasure Map digs wake their guardians", 76,
            "Game/GameClientHandler.TreasureGuardians.cs",
            "Makes a Treasure Map dig wake its guardians. On: after the treasure is paid, one " +
            "guardian appears beside the player on starting maps (10 minutes), or five bosses at " +
            "their lairs on the higher maps (30 minutes). Off: a dig gives loot only. Only the " +
            "digger can fight them; they give EXP but no loot."),
        Switch(Finalise2RateKeys.TotemPoles, "Summoning Totem Poles 4473-4476 call faction soldiers", 77,
            "Game/GameClientHandler.TotemPoles.cs",
            "Makes the Summoning Totem Poles call faction soldiers. On: used within 30 units of " +
            "enemy players, a pole summons 5 soldiers or 3 mages of your faction for 60 seconds. " +
            "Off: the items do nothing. The soldiers fight enemy players only."),
        Switch(Finalise2RateKeys.SacredSealer, "Sacred Sealer sells Ladon / Cyclops / Medusa scrolls", 78,
            "Game/GameClientHandler.SacredSealer.cs",
            "Lets the Sacred Sealer sell boss scrolls. On: he sells your faction's Ladon, Cyclops " +
            "and Medusa Scroll for 180, 900 and 1,800 Gold, and a scroll boss can be fought only " +
            "by its summoner and drops its Copper, Silver or Gold Treasure Chest. Off: he sells " +
            "nothing, and scroll bosses pay 30 Exp Pills to whoever kills them."),
        Switch(Finalise2RateKeys.Autoloot, "Plump Wheat / Golden Leaf autoloot (4468 / 4469)", 73,
            "Game/GameClientHandler.Autoloot.cs",
            "Makes Plump Wheat and Golden Leaf switch on automatic looting. On: Plump Wheat gives " +
            "4 hours and Golden Leaf 7 days; the time left carries over between logins. Off: the " +
            "items do nothing."),
        Switch(Finalise2RateKeys.TreasureMapDigs, "Treasure Map digs at the marked spot (4470 / 4471)", 74,
            "Game/GameClientHandler.TreasureMapDigs.cs",
            "Makes a Treasure Map work at its marked spot. On: used within 10 units of the spot " +
            "in its tooltip, it gives one treasure. Off: the old rule: it works anywhere outside " +
            "the capitals, but only while the Random Quest event is on."),
        // W3 Magic Bell (F-136, the admin 2026-11-09: "anywhere from 10 mins to 70 mins depending on the pet's stats,
        // rank 75 should see the lower bound").
        Switch(Finalise2RateKeys.MagicBell, "Magic Bell: Send Pet on a daily quest (4113)", 74,
            "Game/GameClientHandler.PetDispatch.cs",
            "Makes the Quest Log's Send Pet button work. On: a summoned pet with the Quest " +
            "Dispatch talent takes one daily quest and one Magic Bell and finishes the quest " +
            "after 10 to 70 minutes for its normal reward. Off: the button is refused and Pet " +
            "Merchants sell no bells. Bells cost 30 Gold in the Item Mall and also drop from some " +
            "bosses."),
        // F-143 (the admin 2026-11-09: "30g in mall or in some loot tables 0.5%"): the captured mall already lists the bell
        // at 30 Gold, so the GENERATED 50,000-silver Pet Merchant listing is off by default (a new key, so a database
        // that stored the old price still loses the listing).
        new(Finalise2RateKeys.MagicBellMerchant, Category, "Magic Bell: also sold by the Pet Merchants (silver)", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 75, "Packets/PacketBuilder.MagicBellListing.cs",
            "Lets the Pet Merchants in Athens and Sparta sell the Magic Bell for Silver. On: they " +
            "list it at the price below. Off: bells come only from the Item Mall (30 Gold) and " +
            "boss drops. Needs 'Magic Bell: Send Pet on a daily quest' on."),
        new(Finalise2RateKeys.MagicBellPrice, Category, "Magic Bell: Pet Merchant price", "Silver", ServerRateKind.Absolute,
            50_000m, 1m, 10_000_000m, 1m, 75, "Packets/PacketBuilder.MagicBellListing.cs",
            "Silver price of one Magic Bell at the Pet Merchants. Used while the Pet Merchants " +
            "sell the bell."),
        Switch(Finalise2RateKeys.MagicBellDrops, "Magic Bell: drops from 15 bosses and elites", 75,
            "Application/World/Content/MagicBellLootRule.cs",
            "Lets 15 named bosses and elites (levels 47 to 174) drop a Magic Bell. On: each kill " +
            "has the chance set below, on top of the monster's normal loot. Off: no bell drops. " +
            "The drop chance and drop quantity settings do not change it."),
        new(Finalise2RateKeys.MagicBellDropChance, Category, "Magic Bell: drop chance per kill (50 = 0.5%)", "basis points",
            ServerRateKind.Absolute, 50m, 0m, 10_000m, 1m, 75, "Application/World/Content/MagicBellLootRule.cs",
            "Chance that a listed boss or elite drops a Magic Bell, in hundredths of a percent. " +
            "50 = 0.5%, 100 = 1%, 10,000 = always."),
        // B22 K1 (the admin d43): skill-book chests from the 19 open-map world bosses, 5% per kill, outside the table.
        Switch(Finalise2RateKeys.SkillBookChestDrops, "Skill-book chests: drop from the 19 world bosses", 75,
            "Application/World/Content/SkillBookChestLootRule.cs",
            "Lets the 19 open-map world bosses drop a skill-book chest for their level tier (Lv6 books up to " +
            "boss level 100, Lv7 to 140, Lv8 to 170, Lv9-10 above). On: each kill has the chance set below, on " +
            "top of the boss's normal loot. Off: no chests drop."),
        new(Finalise2RateKeys.SkillBookChestChance, Category, "Skill-book chests: drop chance per boss kill (500 = 5%)",
            "basis points", ServerRateKind.Absolute, 500m, 0m, 10_000m, 1m, 75,
            "Application/World/Content/SkillBookChestLootRule.cs",
            "Chance that a world boss drops its skill-book chest, in hundredths of a percent. 500 = 5%, " +
            "10,000 = always. The drop chance and drop quantity settings do not change it."),
        new(Finalise2RateKeys.MagicBellSlowestMinutes, Category, "Magic Bell: slowest dispatch (rank 0)", "minutes",
            ServerRateKind.Absolute, 70m, 1m, 1440m, 1m, 76, "State/PetQuestDispatchPolicy.cs",
            "Minutes a rank 0 pet takes to finish a quest it was sent on with a Magic Bell. " +
            "Higher ranks are faster, down to the fastest time below."),
        new(Finalise2RateKeys.MagicBellFastestMinutes, Category, "Magic Bell: fastest dispatch", "minutes",
            ServerRateKind.Absolute, 10m, 1m, 1440m, 1m, 77, "State/PetQuestDispatchPolicy.cs",
            "Minutes the fastest pets take to finish a quest they were sent on with a Magic Bell. " +
            "Applies from the rank set below; lower ranks take proportionally longer."),
        new(Finalise2RateKeys.MagicBellFastestRank, Category, "Magic Bell: rank that reaches the fastest time", "rank",
            ServerRateKind.Absolute, 75m, 1m, 655m, 1m, 78, "State/PetQuestDispatchPolicy.cs",
            "Pet rank from which a Magic Bell quest takes the fastest time. Pets below it take " +
            "longer, up to the slowest time at rank 0."),
        Switch(Finalise2RateKeys.PetWork, "Pet work (the pet window's Work button)", 74,
            "Game/GameClientHandler.PetWork.cs",
            "Makes the pet window's Work button work. On: a summoned pet with the Work talent can " +
            "work for 1, 3, 8 or 24 hours; the player pays its food in Silver or Gold and gets " +
            "EXP and Talent Points when it returns (20% if stopped early). Off: the button does " +
            "nothing. Pets already working still come back."),
        Switch(Finalise2RateKeys.ChatBubbles, "Chat Bubble 1 / 2 items (4110 / 4111)", 71,
            "Game/GameClientHandler.ChatBubbles.cs",
            "Makes the Chat Bubble items work. On: using one gives your chat lines that bubble " +
            "style for 3 hours (red for Chat Bubble 1; Chat Bubble 2 looks like the normal bubble " +
            "in this game client). Off: the items do nothing."),
        Switch(Finalise2RateKeys.BeginnerGifts, "Online-time beginner gifts (the gift button)", 69,
            "Game/GameClientHandler.TimePrize.cs",
            "Runs the gift button's online-time gifts for new players. On: the gift window counts " +
            "time online and pays its 8 gifts between 5 and 700 minutes; the items are bound. " +
            "Off: the window stays empty."),
        Switch(Finalise2RateKeys.OfflinePrize, "Offline prize: EXP / TP / energy per hour away (Lv60+, 24 h+)", 74,
            "Game/GameClientHandler.OfflinePrize.cs",
            "Pays a reward for time away. On: a character of level 60 or more who returns after " +
            "24 hours or longer gets EXP, Talent Points and Stored Energy for each full hour " +
            "away, shown in the game's welcome-back box. Off: nothing. The EXP and Talent follow " +
            "the 'Event NPC EXP & Talent' setting."),
        // W4-B (the admin 2026-11-12): login-window awards, Prize Chest, queued buffs.
        Switch(Finalise2RateKeys.LoginAwards, "Login-window awards (editor: Login awards page)", 75,
            "Game/GameClientHandler.LoginAwards.cs",
            "Pays the login awards set up on the editor's Login awards page. On: a character " +
            "entering the world during an award's dates gets it once, into the bag or the Prize " +
            "Chest, with one chat line. Off: nothing is paid. Awards missed while off are paid " +
            "when it is back on, if their dates are still open."),
        Switch(Finalise2RateKeys.QueuedBuffs, "Buffs queued for offline characters", 76,
            "Game/GameClientHandler.LoginAwards.cs",
            "Applies buffs the editor queued for offline characters. On: the buff starts when the " +
            "character next enters the world and ends at logout, like every editor buff. Off: " +
            "queued buffs keep waiting."),
        Switch(Finalise2RateKeys.PrizeChest, "Prize Chest award storage (by the warehouse)", 77,
            "Game/GameClientHandler.PrizeChest.cs",
            "Runs the Prize Chest beside the warehouse. On: talking to it hands over waiting " +
            "prizes (login awards, editor gifts): what fits goes into the bag and wallet, the " +
            "rest keeps waiting. Off: the chest opens an empty window and prizes keep waiting."),
        new(Finalise2RateKeys.PrizeChestWindow, Category, "Prize Chest: show the client's award tab (Draw button)", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 78, "Game/GameClientHandler.PrizeChest.cs",
            "Chooses how the Prize Chest hands over prizes. On: prizes appear in the Storage " +
            "window's award tab. Draw takes all that fits; a right-click or a drag to the bag " +
            "takes one. Off: talking to the chest hands everything over at once."),
        Switch(Finalise2RateKeys.RookieGrowth, "Newbie growth rewards (Rookie Guide button by the minimap)", 74,
            "Game/GameClientHandler.RookieGrowth.cs",
            "Runs the Rookie Guide's growth rewards (the button by the minimap). On: the window " +
            "lists the growth goals this server can check, and Complete pays each reward once per " +
            "character. Off: the window stays empty."),
        new(Finalise2RateKeys.BeginnerGiftsMaxLevel, Category, "Beginner gifts: highest level that earns online time", "level",
            ServerRateKind.Absolute, 200m, 1m, 200m, 1m, 70, "Game/GameClientHandler.TimePrize.cs",
            "Highest level that still earns the online-time beginner gifts. Characters above it " +
            "get no gift window. 200 = everyone."),
        BoardSwitch(Finalise2RateKeys.BoardLoginLine, "Board: online line at login", 1,
            "Sends each player the 'Online: players, Athenians, Spartans' line when they enter " +
            "the world. On: sent at every login; it also switches on the message board in game " +
            "client 1.4.3 and newer. Off: not sent. Keep it on: the welcome and board lines need " +
            "it."),
        BoardSwitch(Finalise2RateKeys.BoardAnnouncements, "Board: show GM announcements", 2,
            "Shows GM announcements on the message board. On: a GM /announce or an announcement " +
            "from the editor also appears on the board above the chat box. Off: it does not."),
        BoardSwitch(Finalise2RateKeys.BoardLotteryHints, "Board: show lottery hints", 3,
            "Shows lottery hints on the message board. On: the Rich Man Envoy's 'lowest unique " +
            "number' hints also appear on the board. Off: they do not."),
        BoardSwitch(Finalise2RateKeys.BoardServerInfo, "Server messages go to the top box, not the chat box", 4,
            "Keeps server messages out of the chat box. On: the online-players line, event NPC " +
            "notices and GM announcements go to the message board above the chat box, leaving the " +
            "chat box for players. Off: they go to World chat. Older game clients without the " +
            "board get them as game log lines."),
        BoardSwitch(Finalise2RateKeys.ChatWorldBossLine, "World boss spawns also in the chat box", 5,
            "Also writes world boss returns in the chat box. On: the notice is added as a World " +
            "chat line, so it stays in the chat history. Off: only the line in the middle of the " +
            "screen. Needs 'World boss spawn notice' on."),
        BoardSwitch(Finalise2RateKeys.EventChatLines, "Event openings also in the chat box", 6,
            "Also writes built-in event opening notices in the chat box. On: notices such as " +
            "Hera's Labyrinth's 'One minute to ...' are added as a World chat line. Off: only the " +
            "line in the middle of the screen. Your own notices are set on the Events & notices " +
            "page."),
        // B21 (item 19): the server time in front of each board message. Done here, not in Board.lua, because the server
        // wraps one message over up to 3 board lines and the stamp must count towards the 46-character width.
        BoardSwitch(Finalise2RateKeys.BoardMessageTime, "Board: show the time of each message", 8,
            "Puts the server time (hours and minutes) in front of each message on the message " +
            "board above the chat box, for example 19:05. On: every new message shows when it " +
            "was sent. Off: messages show without a time. Older game clients get the time in their " +
            "game log line too."),
        // W4F (wave 4 item 4): the client's own Text/SysMsg/WeekDay.Dat opening lines, sent by the server. Default off.
        new(Finalise2RateKeys.EventOpenNotices, BoardCategory, "Event openings: client's own opening notices", "0/1",
            ServerRateKind.Absolute, 1m /* B21 item 17: default on */, 0m, 1m, 1m, 7, "Game/GameSessionRegistry.EventOpeningNotices.cs",
            "Sends the game's own opening notice when an event opens. On: every player gets a " +
            "line in the middle of the screen when Treasure Land, Sicily, Pan's Labyrinth, " +
            "Pindus, Ni Mini Valley or the Lelantine Farm opens, and 1 minute before the Trojan " +
            "Expedition. Off: no notice. Events that are switched off or always open send " +
            "nothing."),
        Switch(Godswar.Server.Application.Mentor.MentorPolicy.MastersEthicsRateKey,
            "Mentor: Masters Ethics instead of per-level B-Gold", 68, "Application/Mentor/MentorPolicy.cs",
            "Chooses what a master earns from an apprentice. On: each level the apprentice gains " +
            "gives the master 10 times that level in Masters Ethics, traded at the Exchange " +
            "Mentor once a month (8,000, 25,000 or 70,000 for 1,380, 2,300 or 3,220 B-Gold). Off: " +
            "20 B-Gold per apprentice level. Graduation rewards stay the same."),
        Switch(Finalise2RateKeys.ReputationExchange, "Exchange Mentor: Reputation for B-Gold (monthly)", 67,
            "Game/GameClientHandler.ReputationExchange.cs",
            "Lets the Exchange Mentor trade Reputation for B-Gold once a month. On: 1,200 " +
            "Reputation pays 920 B-Gold, 5,000 pays 1,600 and 15,000 pays 2,300. Off: the buttons " +
            "do nothing."),
        Switch(Finalise2RateKeys.MermaidDew, "Atlantis Mermaid: high HP, net capture gives a Morning Dew", 65,
            "Application/WorldInstances/AtlantisPetSpawnPolicy.cs",
            "Changes the Atlantis Mermaid. On: she has elite health, and catching her with a " +
            "Mysterious Tuck Net gives the Morning Dew chosen below. Off: the old Mermaid that " +
            "falls in ten hits and gives a Merman egg. The Siren always gives the Merman egg. " +
            "Applies to runs started after the change."),
        new(Finalise2RateKeys.MermaidDewItem, Category, "Atlantis Mermaid: Morning Dew item id", "item",
            ServerRateKind.Absolute, 10132m, 10130m, 10134m, 1m, 66, "Application/WorldInstances/AtlantisPetSpawnPolicy.cs",
            "Which Morning Dew a Mermaid capture gives, as an item number. 10130 to 10134 are " +
            "Morning Dew 1 to 5; the default 10132 is Morning Dew 3."),
        Switch(Finalise2RateKeys.MermaidKillExpStone, "Atlantis Mermaid: kill drops an EXP Stone 4", 66,
            "Application/WorldInstances/AtlantisPetSpawnPolicy.cs",
            "Makes a killed Atlantis Mermaid drop an EXP Stone 4. On: every Mermaid kill drops " +
            "exactly one; the drop chance and drop quantity settings do not change it. Off: no " +
            "drop. Morning Dew only ever comes from the net."),
        Switch(Finalise2RateKeys.PersonalHelper, "Personal Helper: batch item use", 64,
            "Game/GameClientHandler.PersonalHelper.cs",
            "Lets the Personal Helper use many items at once. On: a Morning Dew, EXP Stone, " +
            "Talent Stone, Gold Bag or B-Gold Bag placed with him is used 1 to 99 times in one " +
            "go. Off: he only shows his page."),
        // KELSIS (F-15): OFF by default. A submitted dog is taken from the player (snapshotted first; restore SQL in
        // server/docs/kelsis-contest/), so turning it on is the admin's call.
        // W6-B (the admin 2026-11-13, "all players have a blank buff"): statuses 1500-1507 are this server's own
        // account bonuses. Clients up to 1.5.0 have no Status.ini row for them and draw an empty icon, so the
        // icon stays off until every player has client 1.5.1. The bonus itself never depends on this switch.
        new(Finalise2RateKeys.AccountBonusIcons, Category, "Show buff icons for account bonuses (needs client 1.5.1)", "0/1",
            ServerRateKind.Absolute, 1m /* B21 item 17: default on */, 0m, 1m, 1m, 79, "Packets/ClientStatusWirePolicy.cs",
            "Shows a buff icon for the account bonuses set in the editor: donator tier, Battle " +
            "Pass and the faction area EXP bonus. On: the icon is sent; clients older than 1.5.1 " +
            "draw it blank. Off: no icon. The bonus itself applies either way."),
        new(Finalise2RateKeys.KelsisContest, Category, "Kelsis lowest-stat dog contest (Lelantine Farm)", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 67, "Game/GameClientHandler.KelsisContest.cs",
            "Runs Delighted Kelsis's lowest-stat dog contest at the Lelantine Farm. On: she takes " +
            "a summoned Loyal Dog whose stat for the round is the lowest so far and pays 1 " +
            "Morning Dew; the lowest dog's owner later claims 2,300 B-Gold at the Battlefield " +
            "Awarder. Off: she says nothing. The player loses the dog. Needs 'Battlefield Awarder " +
            "pays rewards' on."),
        Switch(Finalise2RateKeys.HolyStoneRefund, "Holy Stone Redeemer: details, exchange and refund pages", 55,
            "Game/GameClientHandler.HolyStoneRedeemerPages.cs",
            "Gives the Holy Stone Redeemer his three pages. On: details, exchange (a Holy Stone " +
            "Package Voucher becomes a Holy Stone Package) and refund (a voucher is taken back " +
            "for 1,800 Gold). Off: one details page only, which exchanges carried vouchers when " +
            "opened."),
        Switch(Finalise2RateKeys.Lottery, "Rich Man Envoy lottery (Rags to Riches)", 62,
            "Game/GameClientHandler.Lottery.cs",
            "Runs the Rich Man Envoy's lottery (Rags to Riches). On: between 19:00 and 23:00, 10 " +
            "Level 1 Crystals buy an Exp Pill and a number from 1 to 10,000; the lowest number " +
            "nobody else picked wins each round of 100 entries. Off: he only shows his pages."),
        new(Finalise2RateKeys.CupidNecklaceGold, Category, "Cupid sells a Whelk Necklace for (Gold)", "Gold", ServerRateKind.Absolute,
            2300m, 0m, 100000m, 1m, 64, "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "Gold Cupid charges for a Whelk Necklace. 0 = Cupid does not sell it. Pan still gives " +
            "one for free. Needs game client 1.4.5 or newer."),
        new(Finalise2RateKeys.LotteryPrizeGold, Category, "Lottery prize (Gold)", "Gold", ServerRateKind.Absolute,
            500m, 0m, 100000m, 1m, 63, "Game/GameClientHandler.Lottery.cs",
            "Gold paid to the winner of each lottery round. The game does not state a prize, so " +
            "this is our own value."),
        Switch(Finalise2RateKeys.Apothecary, "Apothecary: gems to EXP potions, potion transfer", 61,
            "Game/GameClientHandler.Apothecary.cs",
            "Makes the Apothecary's exchanges work. On: he trades level 1 and 2 gems and level 1 " +
            "Crystals for pet and EXP potions (daily limits apply) and combines 8 potions into 1 " +
            "of the next tier. Off: he only shows his pages."),
        Switch(Finalise2RateKeys.MountFeeder, "Mount Feeder: upgrade, degrade, re-skin, shifter, bind gear", 60,
            "Game/GameClientHandler.MountFeeder.cs",
            "Makes the Mount Feeder's services work. On: he upgrades mounts with Soul Stones, " +
            "degrades them (stones returned), changes their look with a Mount Shifter, exchanges " +
            "level 40 mounts for shifters and binds mount gear. Off: he only shows his pages."),
        // W0 F-111: gold-box dial contents. 0 = today's contents, 1 = original retail dial pools, 2 = combined.
        new(Godswar.Server.State.GiftPackDialPools.RateKey, Category, "Gold box contents (0 current, 1 original, 2 both)",
            "mode", ServerRateKind.Absolute, 0m, 0m, 2m, 1m, 4, "State/GiftPackDialPools.cs",
            "Chooses what the gold boxes can give. 0 = the current contents. 1 = the original " +
            "game's prize lists, with their fixed bonus stats. 2 = both, with even odds between " +
            "the two lists."),
        // FINALISE2b (F-121): OFF by default. Turn on only after client 1.4.2 (Quest.xml MaxLevel 200) is published:
        // with it on, an npc that gives one of these quests opens its quest window for Lv141-200 players.
        new(Finalise2RateKeys.QuestCapsTo200, Category, "Quests capped 121-140 open to Lv200", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 5, "Domain/World/Content/QuestLevelCapRaise.Generated.cs",
            "Opens the 159 quests that stopped at levels 121 to 140 to players up to level 200. " +
            "On: they can be taken up to level 200. Off: the old nine-level range. Needs game " +
            "client 1.4.2 or newer."),
        // ROUND3 F-123: OFF by default. Turn on only after client 1.4.6 (Quest.xml MaxLevel 200 for these ids) is published.
        // The 196 are main story: the free ones were already open from MinLevel up; with it on the 92 exact-level Thebes
        // dailies open from MinLevel to Lv200 too (one a day each, rewards as authored).
        new(Finalise2RateKeys.QuestCaps120To200, Category, "Quests capped 120 open to Lv200", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 5, "Domain/World/Content/QuestLevelCapRaise120.Generated.cs",
            "Opens the 196 main-story quests that stopped at level 120 to players up to level " +
            "200. On: they can be taken up to level 200; the Thebes dailies among them open from " +
            "their own level, and the '[Lv131] Summons (4)' scroll quests from level 126. Off: " +
            "the old level limits. Needs game client 1.4.6 or newer."),
        Switch(Finalise2RateKeys.BGoldTrader, "B-Gold Trader daily exchange", 10,
            "Game/GameClientHandler.Finalise2Npc.cs",
            "Lets the B-Gold Trader exchange Gold for B-Gold. On: he takes 180 Gold once a day " +
            "and pays the amount set below. Off: he only shows his text."),
        new(Finalise2RateKeys.BGoldTraderAmount, Category, "B-Gold Trader: B-Gold paid", "B-Gold",
            ServerRateKind.Absolute, 360m, 0m, 100000m, 1m, 11, "Game/GameClientHandler.Finalise2Npc.cs",
            "B-Gold the B-Gold Trader pays for 180 Gold. The game's own success message always " +
            "says 900, so the server adds a chat line with the real amount."),
        Switch(Finalise2RateKeys.MysteryMerchant, "Mystery Merchant daily goods", 20,
            "Game/GameClientHandler.Finalise2Npc.cs",
            "Lets the Mystery Merchant sell his daily goods. On: Spring Water (Restricted) for " +
            "21,000 Gold once a day, and Morning Dew 2 (Restricted) from 200 Gold, 100 more per " +
            "purchase, up to 30 a day. Off: he sells nothing."),
        Switch(Finalise2RateKeys.CadianPlates, "Cadian Generals take Cadian Plates", 30,
            "Game/GameClientHandler.Finalise2Npc.cs",
            "Lets the Cadian Generals on Marathon and Peloponnese take Cadian Plates for EXP. On: " +
            "they take 1 to 99 plates at a time. Off: they only show their text."),
        new(Finalise2RateKeys.CadianPlateExperience, Category, "Cadian Plate EXP each", "EXP",
            ServerRateKind.Absolute, 1000m, 0m, 1000000m, 1m, 31, "Game/GameClientHandler.Finalise2Npc.cs",
            "EXP paid for each Cadian Plate. The game's text says 1,000. It is paid as set here: " +
            "the 'Event NPC EXP & Talent' setting does not change it."),
        Switch(Finalise2RateKeys.WarSupplierBuyBack, "War Supplier buys back war materials", 40,
            "Game/GameClientHandler.Finalise2Npc.cs",
            "Lets the War Suppliers take back war materials. On: opening a War Supplier hands in " +
            "the Food, Weapon, Medicine and Wine you carry, up to 6 a day, for a reward. Off: " +
            "they only show their page. The reward sizes are our own values."),
        Switch(Finalise2RateKeys.Vouchers, "Holy Box vouchers and Sacrificial Bag", 50,
            "Game/GameClientHandler.Finalise2Items.cs",
            "Makes Holy Box vouchers and the Sacrificial Bag usable. On: a Holy Box II or III " +
            "Voucher gives a full Holy Box II or III, and a Sacrificial Bag donates 500,000 " +
            "Silver to the player's guild. Off: the items do nothing."),
        Switch(Finalise2RateKeys.FactionChangeTeleport, "Faction change: teleport instead of logout", 60,
            "Game/ScriptedNpcDialogueCatalog.IdentityChange.cs",
            "Keeps a player online after a faction change. On: the player is moved to the new " +
            "faction's capital at once. Off: the player is logged out and logs in at the new " +
            "capital."),
        Switch(Finalise2RateKeys.GenderChangeTeleport, "Gender change: re-place on the spot instead of logout", 61,
            "Game/ScriptedNpcDialogueCatalog.IdentityChange.cs",
            "Keeps a player online after a gender change. On: the character is reloaded on the " +
            "same spot, so everyone sees the new look at once. Off: the player is logged out."),
        Switch(Finalise2RateKeys.TalentReset, "Zeus' Baptism resets talents", 70,
            "Game/GameClientHandler.Finalise2Items.cs",
            "Makes Zeus' Baptism reset talents. On: using it returns every spent Talent Point and " +
            "clears all talent ranks. Off: the item does nothing."),
        Switch(Finalise2RateKeys.PetItems, "Pet EXP stones and pet water compound", 80,
            "State/PetExperienceItemPolicy.cs, State/ForgeCompoundPlanner.cs",
            "Makes Pet EXP Stones and the Spring Water recipe work. On: Pet EXP Stones give the " +
            "carried pet EXP like Morning Dew, and 6 Spring Water combine into 1 Juice of Rebirth " +
            "on the forge's Gems tab. Off: neither works."),
        Switch(Finalise2RateKeys.HairItems, "Hairdye and Pretty Wig", 90,
            "Game/GameClientHandler.Finalise2Items.cs",
            "Makes Hairdye and the Pretty Wig work. On: Hairdye:Silver gives silver hair and a " +
            "Pretty Wig moves to the next hair style; the new hair shows at once. Off: the items " +
            "do nothing."),
        // B22 G1 (d38): god gems from the 19 open-map world bosses
        new(Finalise2RateKeys.GodGemDrops, Category, "God gems: drops from the 19 world bosses", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 91, "Application/World/Content/GodGemLootRule.cs",
            "Lets the open-map world bosses drop one of the four Olympian god gems. On: each kill " +
            "has the chance set below, on top of the boss's normal loot. Off (default): no god gems. " +
            "Needs a client that knows items 4910-4913."),
        new(Finalise2RateKeys.GodGemDropChance, Category, "God gems: drop chance per boss kill (200 = 2%)",
            "basis points", ServerRateKind.Absolute, 200m, 0m, 10_000m, 1m, 92,
            "Application/World/Content/GodGemLootRule.cs",
            "Chance that a world boss drops a god gem, in hundredths of a percent. 200 = 2%."),
        // B22 G2 (d36): Class Suit point medals
        new(Finalise2RateKeys.MedalDrops, Category, "Class Suit medals: Valour Laurel and Aether Crest drops", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 93, "Application/World/Content/MedalLootRule.cs",
            "Lv140-170 bosses and elites drop Valour Laurels; the Aether Sanctum instance drops Aether Crests. " +
            "The Class Suit NPC takes them to upgrade Class Suit IV to Titan, Primordial and Aether. Off: no medals."),
        new(Finalise2RateKeys.MedalLaurelEliteChance, Category, "Valour Laurel: chance per Lv140-170 elite (2500 = 25%)",
            "basis points", ServerRateKind.Absolute, 2500m, 0m, 10_000m, 1m, 94, "Application/World/Content/MedalLootRule.cs",
            "Chance that a Lv140-170 elite drops one Valour Laurel, in hundredths of a percent."),
        new(Finalise2RateKeys.MedalLaurelBossCount, Category, "Valour Laurel: Laurels per Lv140-170 boss", "count",
            ServerRateKind.Absolute, 5m, 0m, 99m, 1m, 95, "Application/World/Content/MedalLootRule.cs",
            "How many Valour Laurels a Lv140-170 boss always drops."),
        new(Finalise2RateKeys.MedalCrestNormalChance, Category, "Aether Crest: chance per Aether Sanctum monster (1000 = 10%)",
            "basis points", ServerRateKind.Absolute, 1000m, 0m, 10_000m, 1m, 96, "Application/World/Content/MedalLootRule.cs",
            "Chance that an ordinary Aether Sanctum monster drops one Aether Crest. Elites always drop one."),
        new(Finalise2RateKeys.MedalCrestBossCount, Category, "Aether Crest: Crests per Aether Sanctum boss", "count",
            ServerRateKind.Absolute, 5m, 0m, 99m, 1m, 97, "Application/World/Content/MedalLootRule.cs",
            "How many Aether Crests an Aether Sanctum boss always drops (bosses come back after an hour)."),
        // B22 G2 (d38): Radiant / Primal gems from the Aether Sanctum
        new(Finalise2RateKeys.SanctumGemDrops, Category, "Rare gems: Radiant and Primal drops in the Aether Sanctum", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 98, "Application/World/Content/SanctumGemLootRule.cs",
            "Aether Sanctum monsters drop the Radiant (grade 21-22) and Primal (grade 23-24) gems that forge Titan, " +
            "Primordial and Aether gear. Off: no such gems."),
        new(Finalise2RateKeys.RadiantNormalChance, Category, "Radiant gem: chance per Sanctum monster (100 = 1%)",
            "basis points", ServerRateKind.Absolute, 100m, 0m, 10_000m, 1m, 99, "Application/World/Content/SanctumGemLootRule.cs",
            "Chance that an ordinary Aether Sanctum monster drops a Radiant gem."),
        new(Finalise2RateKeys.RadiantEliteChance, Category, "Radiant gem: chance per Sanctum elite (2500 = 25%)",
            "basis points", ServerRateKind.Absolute, 2500m, 0m, 10_000m, 1m, 100, "Application/World/Content/SanctumGemLootRule.cs",
            "Chance that an Aether Sanctum elite drops a Radiant gem (when it drops no Primal gem)."),
        new(Finalise2RateKeys.PrimalEliteChance, Category, "Primal gem: chance per Sanctum elite (500 = 5%)",
            "basis points", ServerRateKind.Absolute, 500m, 0m, 10_000m, 1m, 101, "Application/World/Content/SanctumGemLootRule.cs",
            "Chance that an Aether Sanctum elite drops a Primal gem."),
        new(Finalise2RateKeys.PrimalBossCount, Category, "Primal gem: gems per Sanctum boss", "count",
            ServerRateKind.Absolute, 1m, 0m, 99m, 1m, 102, "Application/World/Content/SanctumGemLootRule.cs",
            "How many Primal gems an Aether Sanctum boss always drops.")
    ];
}
