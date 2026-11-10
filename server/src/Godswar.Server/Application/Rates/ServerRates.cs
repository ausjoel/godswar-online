using System.Collections.Frozen;

namespace Godswar.Server.Application.Rates;

// RATES: server-wide multipliers and limits (GodsWar-2.51/Polish patch).
//
// One table (public.server_rates) holds every value; the game server reads it
// at start-up and hot-reloads it (poll every few seconds, or POST /rates/reload
// on the loopback management port). Every multiplier defaults to 1 and every
// limit to the value the stock server hard-codes, so an empty or missing table
// leaves gameplay byte-for-byte unchanged. Gameplay code reads
// ServerRates.Current at the moment a reward or limit is decided; nothing is
// cached elsewhere, so a reload applies to the next kill / hand-in / tick.

internal enum ServerRateKind
{
    /// <summary>A factor applied to a stock amount or chance (1 = stock).</summary>
    Multiplier = 1,

    /// <summary>An absolute limit that replaces a stock constant.</summary>
    Absolute = 2
}

internal sealed record ServerRateDefinition(
    string Key,
    string Category,
    string Label,
    string Unit,
    ServerRateKind Kind,
    decimal DefaultValue,
    decimal MinimumValue,
    decimal MaximumValue,
    decimal Step,
    int SortOrder,
    string AppliesTo,
    string Description);

internal static class ServerRateKeys
{
    public const string MonsterExperience = "exp.monster";
    public const string MonsterTalentExperience = "tp.monster";
    public const string QuestExperience = "exp.quest";
    public const string QuestTalent = "tp.quest";
    public const string EventExperience = "exp.events";
    public const string PetMonsterExperience = "pet.exp_monster";
    public const string ZodiacEnergy = "zodiac.energy";
    // TITLES: Title Expert / Fortune Teller switches, the Fortune Teller's gold price and the
    // Lucky Day bonus (Application/Titles/TitleExpertPolicy.cs, Application/Zodiac/FortuneTellerPolicy.cs).
    public const string TitleExpertEnabled = "npc.title_expert";
    public const string FortuneTellerEnabled = "npc.fortune_teller";
    public const string FortuneTellerGold = "npc.fortune_teller_gold";
    public const string ZodiacLuckyDayBonus = "zodiac.lucky_day_bonus";
    public const string AtlantisHardPoints = "instance.atlantis_hardpoints";
    public const string MedusaHonor = "instance.medusa_honor";
    public const string FarmKillPoints = "instance.farm_points";
    // ECONOMY r10: Wonderland island-clear Medals (1 = IGG guide table, 0 = off).
    public const string WonderlandMedals = "instance.wonderland_medals";
    // INSTANCES: entry switches for the instances that are still being built.
    public const string HarborAttackEntry = "instance.harbor_attack_entry";
    public const string TrainingGroundEntry = "instance.training_ground_entry";
    // WARRIOR (F-51): the Warriors' Training Ground (Instance Caller root 13), closed by default.
    public const string WarriorTrainingEntry = "instance.warrior_training_entry";
    public const string HarborAttackRestartRefund = "instance.harbor_attack_restart_refund";
    // BAY2: Bay Under Attack completion EXP, percent of one level at full clearance (GENERATED).
    public const string HarborAttackExperiencePercent = "instance.harbor_attack_exp_pct";
    public const string MovementSpeed = "move.speed";
    public const string DropChance = "drop.chance";
    public const string DropQuantity = "drop.quantity";
    public const string DropMaximumItems = "drop.max_items";
    // F-166: total drops one corpse may list (table + extras); never above the client's 8 slots.
    public const string DropCorpseMaximumItems = "drop.corpse_max_items";
    // LOOT PAGING (F-166 note): 1 = one 10029 window (stock), 2 = a second page after the first is picked up.
    public const string DropCorpsePages = "drop.corpse_pages";
    // ATTR: rarity shifts for random-attribute drops (loot_roll_profiles).
    public const string DropRareAttributes = "drop.attr_rare";
    public const string DropQualityUp = "drop.quality_up";
    public const string DropGradeUp = "drop.grade_up";
    public const string QuestSilver = "money.quest_silver";
    public const string QuestGold = "money.quest_gold";
    public const string SellPrice = "money.sell_price";
    public const string ForgeSuccess = "forge.success";
    public const string HolyStoneSuccess = "forge.holystone_success";
    public const string HolyBoxDailyLimit = "holybox.daily_limit";
    public const string HolyBoxPerStoreMaximum = "holybox.per_store_max";
    public const string HolyBoxCapacity = "holybox.capacity";
    public const string HolyBoxMinimumLevel = "holybox.min_level";
    // VILLAGERS: Festival Envoy and Village Leader switches (Application/Villagers).
    public const string WeekendExperienceClaim = "event.weekend_exp_claim";
    public const string WeekendExperienceAnyDay = "event.weekend_exp_any_day";
    public const string FestivalGifts = "event.festival_gifts";
    public const string VillageLeaderBooks = "npc.village_leader_books";
    // ROUND2: costs, item EXP, daily caps and event windows.
    public const string TalentUpgradeCost = "cost.talent";
    public const string ZodiacLevelCost = "cost.zodiac_level";
    public const string ZodiacGridCost = "cost.zodiac_grid";
    public const string FactionCrierExperience = "exp.faction_crier";
    public const string FactionCrierTalent = "tp.faction_crier";
    public const string ExperiencePill = "exp.pill";
    public const string PetExperienceItems = "pet.exp_items";
    public const string WildPetCapture = "pet.wild_capture"; // petcapture
    public const string TreasureLandCapture = "pet.treasure_land_capture"; // PETNET
    public const string LuckyGodsDaily = "cap.lucky_gods_daily";
    public const string WishingPoolDaily = "cap.wishing_pool_daily";
    public const string ZeusGiftsWeekday = "cap.zeus_gifts_weekday";
    public const string ZeusGiftsWeekend = "cap.zeus_gifts_weekend";
    public const string ZeusExchangesPerWeek = "cap.zeus_exchanges_week";
    public const string QuestDailyCompletions = "cap.quest_daily";
    public const string PindusAlwaysOpen = "event.pindus_always_open";
    public const string NiMiniAlwaysOpen = "event.nimini_always_open";
    // FINALISE-PETS: the Lelantine Farm follows its Saturday window unless this is 1.
    public const string LelantineAlwaysOpen = "event.lelantine_always_open";
    public const string BattlefieldWindowMinutes = "event.battlefield_minutes";
    public const string BattlefieldShiftHours = "event.battlefield_shift_hours";
    public const string WonderlandCloseHour = "event.wonderland_close_hour";
    public const string ZeusStoneAnyDay = "event.zeus_stone_any_day";
    public const string SpyInvasion = "event.spy_invasion";
    // CLASSCHANGE: the Class Shifter's switch and its free-level limit.
    public const string ClassChangeEnabled = "npc.class_change";
    public const string ClassChangeFreeLevel = "npc.class_change_free_level";
    public const string ClassChangeInPlaceRefresh = "npc.class_change_refresh"; // FINALISE
    public const string WorldBossNotice = "event.world_boss_notice"; // FINALISE
    public const string GmObeyGates = "gm.obey_gates"; // FINALISE
    public const string BagGainLog = "log.bag_gains"; // FINALISE
    public const string KillGainLog = "log.kill_gains"; // FINALISE2 (F-63)
    public const string PetGainLog = "log.pet_gains"; // FINALISE2 (F-63)
    // PARTY (r02): kill EXP and kill-quest sharing inside a party.
    public const string PartyKillShare = "party.kill_share";
    public const string PartyKillShareCells = "party.kill_share_cells";
    public const string PlayerTrade = "trade.player"; // TRADE
    public const string PlayerStalls = "trade.stalls"; // TRADE
    // BATTLEFIELD: the shared Battlefield Awarder claim flow and Pindus / Ni Mini scoring.
    public const string BattlefieldAwarder = "battlefield.awarder";
    public const string BattlefieldScoring = "battlefield.scoring";
    public const string BattlefieldClaimHours = "battlefield.claim_hours";
    public const string BattlefieldTitleDays = "battlefield.title_days";
    public const string BattlefieldReputation = "battlefield.reputation";
    public const string BattlefieldPindusKillPoints = "battlefield.pindus_kill_points";
    public const string BattlefieldNiMiniIggTimes = "battlefield.nimini_igg_times";
    // BATTLEFIELD2: Pindus at the IGG times, the 60-200 level band, and Awarder lines showing the paid amounts.
    public const string BattlefieldPindusIggTimes = "battlefield.pindus_igg_times";
    public const string BattlefieldPindusBand = "battlefield.pindus_band";
    public const string BattlefieldShowPaidAmounts = "battlefield.show_paid_amounts";
    // MARRIAGE: Cupid / Cupid's Shadow switch and prices (Application/Characters/MarriagePolicy.cs).
    public const string MarriageEnabled = "npc.marriage";
    public const string MarriageNecklaceRequired = "npc.marriage_necklace";
    public const string MarriageEngagementSilver = "npc.marriage_engage_silver";
    public const string MarriageBookingGold = "npc.marriage_booking_gold";
    public const string EventBoard = "event.board"; // EVENTSBOARD
}

internal static class ServerRateCatalog
{
    public const string CharacterCategory = "Character EXP & Talent";
    public const string PetCategory = "Pets";
    public const string ZodiacCategory = "Zodiac";
    public const string InstanceCategory = "Instances";
    public const string DropCategory = "Monster drops";
    public const string MoneyCategory = "Money";
    public const string ForgeCategory = "Forging";
    public const string HolyBoxCategory = "Holy Box (Master Vestment Forger)";
    public const string VillagersCategory = "Festival Envoy and Village Leader"; // VILLAGERS
    public const string CostCategory = "Upgrade costs"; // ROUND2
    public const string DailyCategory = "Daily and weekly limits"; // ROUND2
    public const string EventCategory = "Event times"; // ROUND2
    public const string NpcServiceCategory = "NPC services"; // CLASSCHANGE
    public const string PartyCategory = "Party"; // PARTY (r02)
    public const string TradeCategory = "Player trade and stalls"; // TRADE
    public const string BattlefieldCategory = "Battlefields"; // BATTLEFIELD
    public const string MarriageCategory = "Marriage"; // MARRIAGE

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        .. Godswar.Server.Application.Guilds.GuildRateKeys.Definitions, // GUILD
        .. Godswar.Server.Application.Seasons.OrientalCarnivalSeason.Definitions, // W3 seasons oriental/carnival
        .. Godswar.Server.Application.Seasons.SpringFestivalRules.RateDefinitions, // ROUND3 W3 Spring Festival
        Multiplier(
            ServerRateKeys.MonsterExperience, CharacterCategory, 10,
            "Monster kill EXP", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Progression.cs PrepareMonsterKillRewardAsync",
            "Fighter EXP from a monster kill. Applied after the stock tier table " +
            "(20..279 x4), the 20-level gap rule, EXP potions/boost statuses and " +
            "the database global multiplier (monster_reward_settings, 1-5x, " +
            "restart). One kill is capped at 100,000,000 EXP."),
        Multiplier(
            ServerRateKeys.MonsterTalentExperience, CharacterCategory, 20,
            "Monster kill Talent EXP (TP)", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Progression.cs PrepareMonsterKillRewardAsync",
            "Talent EXP from a monster kill: stock 10 per eligible kill, and " +
            "100 Talent EXP = 1 Talent Point (TP). Applied after Talent boost " +
            "statuses. 2 = a Talent Point every 5 kills instead of 10."),
        Multiplier(
            ServerRateKeys.QuestExperience, CharacterCategory, 30,
            "Quest reward EXP", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "EXP paid when a quest is handed in (after the quest-appraisal " +
            "+10% bonus and any GM-tool quest_reward override)."),
        Multiplier(
            ServerRateKeys.QuestTalent, CharacterCategory, 40,
            "Quest reward Talent", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Talent amount paid when a quest is handed in (credited as Talent " +
            "EXP, 100 = 1 TP, exactly as the stock hand-in does)."),
        Multiplier(
            ServerRateKeys.EventExperience, CharacterCategory, 50,
            "Event NPC EXP & Talent", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.LuckyGods.cs, Game/GameClientHandler.ZeusGift.cs",
            "EXP paid by the Lucky Gods wish pool (210,000..13,490,000 per " +
            "claim, 10 wishes a day) and EXP + Talent paid by Zeus' Gift " +
            "praying stones (8,000 EXP and 1 Talent each, x2 at weekends)."),
        Multiplier(
            ServerRateKeys.PetMonsterExperience, PetCategory, 10,
            "Pet EXP from monster kills", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Progression.cs -> PostgresMonsterRewardExtrasStore",
            "EXP credited to the summoned pet for a kill (stock = the monster's " +
            "tier EXP, plus pet EXP potions). Pets level up with Upgrade from " +
            "that pool (1..120, 252,947,820 EXP in total), so 3 = levelling " +
            "three times as fast. Capped at 100,000,000 per kill."),
        Multiplier(
            ServerRateKeys.ZodiacEnergy, ZodiacCategory, 10,
            "Zodiac energy gain", 0m, 1_000_000m, // ROUND2: max
            "State/ZodiacEnergyAccrual.cs AddEnergyX100",
            "Zodiac energy from online time (stock: 10 per 5-minute tick, 20 " +
            "per tick for the first 3 hours of each realm day, 1 hour of " +
            "compensation after a missed day). Still capped by the storage " +
            "limit of the character's zodiac level."),
        // TITLES: off by default, so shipping the binary changes nothing.
        Absolute(
            ServerRateKeys.ZodiacLuckyDayBonus, ZodiacCategory, 20,
            "Zodiac Lucky Day EXP bonus", "%", 5m, 0m, 100m, 1m,
            "Application/Zodiac/FortuneTellerPolicy.cs ApplyLuckyBonus",
            "Extra EXP and Talent EXP from monster kills while a Fortune Teller " +
            "Lucky Day is active (24 hours). Retail: 5. Only used while " +
            "npc.fortune_teller is 1."),
        Absolute(
            ServerRateKeys.TitleExpertEnabled, "NPC services", 30,
            "Title Expert grants titles", "0/1", 0m, 0m, 1m, 1m,
            "Game/ScriptedNpcDialogueCatalog.TitlesFortune.cs",
            "1 = the Title Expert (Athens/Sparta) grants leveling and attribute " +
            "titles and quest titles are granted on hand-in (IGG Title System). " +
            "0 = menu only (same as leaving GODSWAR_TITLE_EXPERT unset)."),
        Absolute(
            ServerRateKeys.FortuneTellerEnabled, "NPC services", 40,
            "Fortune Teller works", "0/1", 0m, 0m, 1m, 1m,
            "Game/ScriptedNpcDialogueCatalog.TitlesFortune.cs",
            "1 = the Fortune Teller changes the zodiac sign (1 free per month) " +
            "and draws Lucky Days (1 free per week, +EXP for 24 hours). 0 = menu " +
            "only (same as leaving GODSWAR_FORTUNE_TELLER unset)."),
        Absolute(
            ServerRateKeys.FortuneTellerGold, "NPC services", 50,
            "Fortune Teller gold price", "Gold", 230m, 0m, 1_000_000m, 1m,
            "Application/Zodiac/FortuneTellerPolicy.cs",
            "Gold for an extra sign change or an extra Lucky Day. The client's " +
            "buttons say 230 Gold (the GodsArena wiki says 345)."),
        Multiplier(
            ServerRateKeys.AtlantisHardPoints, InstanceCategory, 10,
            "Atlantis honour (HardPoints)", 0m, 10_000m, // ROUND2: max
            "Application/WorldInstances/AtlantisCompletionRewardContracts.cs",
            "HardPoints (shown as Honour) paid when an Atlantis run ends: 200.." +
            "2,400 by team score, 2,800 + title on completion."),
        Multiplier(
            ServerRateKeys.MedusaHonor, InstanceCategory, 20,
            "Medusa Island honour", 0m, 10_000m, // ROUND2: max
            "Application/WorldInstances/MedusaCompletionRewardPolicy.cs",
            "Honour paid when a Medusa Island run ends (database tiers " +
            "medusa_completion_reward_rules: 300..1,350 by score, 975..2,250 " +
            "by clear time). Titles are unchanged."),
        Multiplier(
            ServerRateKeys.FarmKillPoints, InstanceCategory, 30,
            "Lelantine Farm kill points", 0m, 1_000m, // ROUND2: max
            "Game/GameClientHandler.LelantineFarmKills.cs",
            "Points a Lelantine Farm kill scores (1 normal, 10 elite, 1,000 " +
            "Cerberus). Egg donations are not changed."),
        // ECONOMY r10. RELEASE: default 0 (off) instead of the Multiplier helper's 1,
        // and sort order 45 (40 is Bay Under Attack entry).
        new ServerRateDefinition(
            ServerRateKeys.WonderlandMedals, InstanceCategory,
            "Wonderland Medals per island", "x", ServerRateKind.Multiplier,
            0m, 0m, 100m, 0.05m, 45,
            "Application/WorldInstances/WonderlandMedalPolicy.cs",
            "Medals (Point Exchanger balance) paid to every finisher when a " +
            "Wonderland island clears: 50, 130, 80, 90, 100, 110, 120, 570 by " +
            "island (IGG guide boss table, 1,250 a full run) times this value. " +
            "0 = off (release default); 1 = the guide table."),
        // INSTANCES: 0 = closed, so nobody spends a daily entry on an instance
        // that is not finished. The Instance Caller answers its pages with the
        // client's own "remaining today is 0" line plus a note.
        // BAY2: Bay Under Attack is built (the admin 2026-11-03 round 2): OPEN by default.
        Absolute(
            ServerRateKeys.HarborAttackEntry, InstanceCategory, 40,
            "Bay Under Attack entry open", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.InstanceCaller.cs (Instance Caller page 16 / 230)",
            "1 = the Instance Caller lets a party of level 50-90 into Bay Under " +
            "Attack (maps 208/209; default). 0 = the page refuses before any " +
            "daily entry is used."),
        Absolute(
            ServerRateKeys.TrainingGroundEntry, InstanceCategory, 50,
            "Training Ground entry open", "0/1", 0m, 0m, 1m, 1m,
            "Game/GameClientHandler.InstanceCaller.cs (Instance Caller pages 13, 17 / 231)",
            "1 = the Instance Caller's Trials of Hercules page (231) opens " +
            "the Training Ground (map 210 run, 300 points, 30 min, 1 a day; " +
            "needs training-spawns.sql). 0 = pages 17/231 refuse."),
        // WARRIOR (F-51): closed until the spawns are loaded and the admin opens it.
        Absolute(
            ServerRateKeys.WarriorTrainingEntry, InstanceCategory, 55,
            "Warriors' Training Ground entry open", "0/1", 0m, 0m, 1m, 1m,
            "Game/GameClientHandler.InstanceCaller.cs (Instance Caller root 13 / 200, 201, 202, 301)",
            "1 = the Instance Caller's Training Ground page (root 13) opens the " +
            "four Warriors' Training Grounds (maps 201/202/203/206, 200 points, " +
            "20 min, each once a day; needs warrior-spawns.sql). 0 = root 13 refuses " +
            "before any daily entry is used."),
        Absolute(
            ServerRateKeys.HarborAttackRestartRefund, InstanceCategory, 60,
            "Bay Under Attack restart refund", "0/1", 1m, 0m, 1m, 1m,
            "Infrastructure/PostgresApplicationDataRuntime.cs (start-up)",
            "PROJECT RULE (not retail for this instance; extends the wiki's " +
            "rollback list, which leaves Bay Under Attack out). 1 = at " +
            "start-up, every Bay Under Attack run that a server stop or crash " +
            "interrupted, and that Christina's kill had not completed, gives " +
            "all its members their daily entry back (runs from the last 36 " +
            "hours, a generated window; default since BAY2, the admin 2026-11-03). " +
            "A run that paid its completion EXP is never refunded. 0 = no refund."),
        // BAY2: GENERATED completion EXP (the guide gives no amount).
        Absolute(
            ServerRateKeys.HarborAttackExperiencePercent, InstanceCategory, 65,
            "Bay Under Attack completion EXP (% of a level)", "%", 100m, 0m, 1_000m, 5m,
            "Game/GameSessionRegistry.HarborAttackRewards.cs (run end)",
            "GENERATED. Each member still inside when the run ends gets clearance " +
            "x this percent of the EXP his level needs for the next level " +
            "(100 = a full clear is worth one level). Level 200 gets none. " +
            "Read when the run ends."),
        // VILLAGERS: own category, so the ROUND2 event-category count is unchanged.
        .. Application.Villagers.VillagersRateDefinitions.All,
        .. Application.North.NorthRateDefinitions.All, // FINALISE-NORTH
        Multiplier(
            ServerRateKeys.DropChance, DropCategory, 10,
            "Drop chance", 0m, 1_000_000m, // ROUND2: max
            "Application/World/Content/MonsterLootContentSnapshot.cs, " +
            "Application/WorldInstances/MedusaMonsterContentSnapshot.cs",
            "Chance of every loot rule of field/dungeon monsters " +
            "(monster_loot_rules) and Medusa monsters. Capped at 100% per rule."),
        Multiplier(
            ServerRateKeys.DropQuantity, DropCategory, 20,
            "Drop quantity", 1m, 1_000_000m, // ROUND2: max
            "Application/World/Content/MonsterLootContentSnapshot.cs, " +
            "Application/WorldInstances/MedusaMonsterContentSnapshot.cs",
            "Quantity of each dropped item (capped at 255 per drop; pickup " +
            "splits it into stacks)."),
        Multiplier(
            ServerRateKeys.DropMaximumItems, DropCategory, 30,
            "Maximum items per kill", 1m, 1_000_000m, // ROUND2: max
            "Application/World/Content/MonsterLootContentSnapshot.cs",
            "How many different items one field-monster kill may drop " +
            "(monster_loot_tables.maximum_drops, capped at 32 and at the " +
            "monster's number of loot rules). The corpse still lists at most " +
            "drop.corpse_max_items (8): the best ones are kept."),
        // F-166 (b): the per-corpse total. A SAFETY cap: the client's 10029 parser overflows at 9+ entries.
        Absolute(
            ServerRateKeys.DropCorpseMaximumItems, DropCategory, 35,
            "Maximum drops per corpse (all sources)", "items", 8m, 1m, 8m, 1m,
            "Game/GameSessionRegistry.MonsterLoot.cs, Application/World/Content/CorpseLootLimit.cs",
            "the admin F-166 (b): the most drops one corpse lists, counting its loot " +
            "table, the Cerberus book, season and spring drops, the Magic Bell " +
            "and runtime-spawn lines. When more roll, the best are kept (higher " +
            "quality, then rarer chance, then higher value) and the commonest " +
            "overflow is dropped. Capped at 8 in code: the client's loot window " +
            "has 8 slots and 9 or more entries crash it. Read at each kill."),
        // LOOT PAGING (F-166 note "a 16 loot window"): default 1 = off, exactly the single 8-slot window.
        Absolute(
            ServerRateKeys.DropCorpsePages, DropCategory, 36,
            "Loot pages per corpse", "pages", 1m, 1m, 2m, 1m,
            "Game/GameSessionRegistry.MonsterLoot.cs, Game/CorpseLootPages.cs",
            "1 (default) = one loot window of at most drop.corpse_max_items " +
            "drops, as before. 2 = a corpse may keep up to twice that (16 at " +
            "most, the best ones): the first 8 show as today and, once all of " +
            "them are picked up, the corpse shows a second page with the rest " +
            "(the 20 s corpse window restarts for it). Pick All takes one page " +
            "at a time. Never more than 8 per window (client limit). Read at " +
            "each kill."),
        // ATTR: random attributes, quality and grade on profile drops.
        Multiplier(
            ServerRateKeys.DropRareAttributes, DropCategory, 40,
            "Rare attribute rolls (3-4 attributes)", 0m, 100m,
            "Application/World/Content/LootRollProfiles.cs",
            "Scales the weight of 3 and 4 random attributes on gear drops " +
            "whose loot rule names a roll profile (loot_roll_profiles). " +
            "2 = twice as likely, 0 = never. Rules without a profile are " +
            "not affected."),
        Multiplier(
            ServerRateKeys.DropQualityUp, DropCategory, 50,
            "Higher quality on gear drops", 0m, 10m,
            "Application/World/Content/LootRollProfiles.cs",
            "Scales the profile's quality step (the chance of each next " +
            "quality level relative to the one below) on roll-profile gear " +
            "drops. Still capped by the profile's quality range."),
        Multiplier(
            ServerRateKeys.DropGradeUp, DropCategory, 60,
            "Higher grade on gear drops", 0m, 10m,
            "Application/World/Content/LootRollProfiles.cs",
            "Scales the profile's grade step on roll-profile gear drops. " +
            "Still capped by the profile's grade range."),
        Multiplier(
            ServerRateKeys.QuestSilver, MoneyCategory, 10,
            "Quest reward Silver", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Silver paid when a quest is handed in."),
        Multiplier(
            ServerRateKeys.QuestGold, MoneyCategory, 20,
            "Quest reward Gold", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Gold (premium currency, character_base.\"Stone\") paid by quests " +
            "that pay Gold."),
        Multiplier(
            ServerRateKeys.SellPrice, MoneyCategory, 30,
            "NPC sell price", 0m, 5m, // ROUND2: max
            "Infrastructure/Inventory/PostgresCapitalShopPurchaseStore.CapitalShopSale.cs",
            "Silver received when selling to a capital NPC shop (stock 20% of " +
            "the item's value). The client's own price tooltip still shows " +
            "the stock value."),
        Multiplier(
            ServerRateKeys.ForgeSuccess, ForgeCategory, 10,
            "Forge success chance", 0m, 100m, // ROUND2: max
            "State/EquipmentForgeCalculator.cs",
            "Success chance of Ruby / Sapphire / Emerald equipment forging " +
            "(stock base chance + material and odds-item bonuses), capped at " +
            "100%."),
        Multiplier(
            ServerRateKeys.HolyStoneSuccess, ForgeCategory, 20,
            "Holy stone upgrade success", 0m, 100m, // ROUND2: max
            "State/HolyStoneUpgradePolicy.cs",
            "Holy stone upgrade chance (stock 90% / 25% / 10% for levels " +
            "1-3 / 4-6 / 7-9, +10% with a Goddess stone or Signet), capped at " +
            "100%."),
        Absolute(
            ServerRateKeys.HolyBoxDailyLimit, HolyBoxCategory, 10,
            "Daily EXP that can be stored", "EXP", 2_000_000_000m, 0m,
            1_000_000_000_000m, 1_000_000m,
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Store.cs, .Quota.cs",
            "Fighter EXP one account may put into Holy Boxes per realm day " +
            "(Asia/Singapore midnight). An active battle_pass entitlement " +
            "still bypasses it. The client's counter display saturates at " +
            "214,748,364; the server enforces the exact value."),
        .. Godswar.Server.Application.Seasons.EasterAnniversarySeason.Definitions, // W3 seasons easter/igg
        .. Godswar.Server.Application.EventsNpc.EventsNpcRates.Definitions, // EVENTSNPC
        .. Godswar.Server.Application.Finalise2.Finalise2Rates.Definitions, // FINALISE2
        .. Godswar.Server.Application.Troy.TroyEvent.Definitions, // W2 F-27 TROY
        .. Godswar.Server.Application.Troy.TroyBossChain.Definitions, // W2 F-27 TROY slice 2: heroes and chests
        .. Godswar.Server.Application.Seasons.MidAutumnSeason.Definitions, // W3 seasons mid-autumn
        Absolute(
            ServerRateKeys.HolyBoxPerStoreMaximum, HolyBoxCategory, 20,
            "Maximum EXP per Store action", "EXP", 400_000_000m, 1m,
            2_147_483_647m, 1_000_000m,
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Store.cs",
            "Largest amount one Storing EXP request may move (typed amount or " +
            "Store Maximum)."),
        .. Godswar.Server.Application.Seasons.HalloweenMayDaySeason.Definitions, // W3 seasons halloween/may day
        Multiplier(
            ServerRateKeys.HolyBoxCapacity, HolyBoxCategory, 30,
            "Holy Box capacity", 0.1m, 1_000_000m, // ROUND2: max
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Store.cs",
            "Capacity of every Holy Box (stock I 100,000 / II 1,000,000 / III " +
            "10,000,000 / IV 100,000,000 / V 400,000,000). 5 = Holy Box V " +
            "holds 2,000,000,000 (the item EXP field is a 32-bit integer)."),
        Absolute(
            ServerRateKeys.HolyBoxMinimumLevel, HolyBoxCategory, 40,
            "Minimum level (all 4 forger pages)", "level", 70m, 1m, 200m, 1m,
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Planning.cs",
            "Character level required for Storing / Transferring EXP, Ware " +
            "Consuming and Transform EXP. Gear still needs level 70."),
        // ROUND2: upgrade costs (0 = free).
        Multiplier(
            ServerRateKeys.TalentUpgradeCost, CostCategory, 10,
            "Talent upgrade cost", 0m, 1_000m, // ROUND2: max
            "Application/Talents/TalentProgression.cs CalculateUpgradeCost",
            "Talent Points one talent rank costs (stock 1..10 for ranks 1-10, " +
            "rising to several thousand near rank 100). 0.5 = half price. The " +
            "client's talent window still prints the stock price."),
        Multiplier(
            ServerRateKeys.ZodiacLevelCost, CostCategory, 20,
            "Zodiac level-up energy cost", 0m, 1_000m, // ROUND2: max
            "State/ZodiacLevelUpgrade.cs ZodiacLevelUpgradeCatalog",
            "Zodiac energy one zodiac level costs (stock 720 for level 2 up to " +
            "millions near level 30). The zodiac window prints the stock cost " +
            "from the client's own Constellation.lua."),
        Multiplier(
            ServerRateKeys.ZodiacGridCost, CostCategory, 30,
            "Zodiac grid upgrade cost", 0m, 1_000m, // ROUND2: max
            "State/ZodiacSkillGridUpgrade.cs ZodiacSkillGridUpgradeCatalog",
            "Zodiac energy AND Talent Points one zodiac skill-grid level costs. " +
            "The grid tooltip shows the stock cost."),
        // ROUND2: other EXP sources.
        Multiplier(
            ServerRateKeys.FactionCrierExperience, CharacterCategory, 60,
            "Faction Crier EXP", 0m, 1_000_000m, // ROUND2: max
            "Application/FactionCrier/FactionCrierRewardPolicy.cs",
            "EXP paid when nameplates are exchanged at the Faction Crier."),
        Multiplier(
            ServerRateKeys.FactionCrierTalent, CharacterCategory, 70,
            "Faction Crier Talent Points", 0m, 1_000_000m, // ROUND2: max
            "Application/FactionCrier/FactionCrierRewardPolicy.cs",
            "Talent Points paid when nameplates are exchanged at the Faction " +
            "Crier."),
        Multiplier(
            ServerRateKeys.ExperiencePill, CharacterCategory, 80,
            "EXP pill (item 4174)", 0.05m, 1_000_000m, // ROUND2: max
            "State/PlayerExperienceItemPolicy.cs",
            "EXP one EXP pill gives (stock 1,000,000). The old EXP scrolls " +
            "4620-4623 do nothing on this server at all (not implemented " +
            "upstream), so they have no setting."),
        Multiplier(
            ServerRateKeys.PetExperienceItems, PetCategory, 20,
            "Pet EXP items (Morning Dew)", 0.05m, 1_000_000m, // ROUND2: max
            "Infrastructure/Pets/PostgresPetDurableCommandExecutor.ExperienceItem.cs",
            "EXP a Morning Dew (10130-10134, 10140-10144) gives the summoned " +
            "pet. Capped at the pet's 4,294,967,295 EXP field."),
        Absolute( // petcapture
            ServerRateKeys.WildPetCapture, PetCategory, 30,
            "Wild baby pet capture", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/WildPetCapturePolicy.cs",
            "1 = baby Flower Pixies and Minotaurs on the suburbs, Parnitha, " +
            "Nemea, Marathon, Peloponnese, Argolis and Megara can be caught " +
            "with the Wooden..Gold tools and the Mysterious Tuck Net. 0 = off."),
        Absolute( // PETNET: Treasure Land net capture
            ServerRateKeys.TreasureLandCapture, PetCategory, 40,
            "Treasure Land net capture", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/TreasureLandCapturePolicy.cs",
            "1 = Treasure Land (map 25) monsters can be netted with the Wooden..Gold " +
            "tools and the Mysterious Tuck Net for a Simple / Greater / Superior " +
            "Travel Pack. 0 = off. The monsters also need GODSWAR_TREASURE_LAND_SPAWNS=1 " +
            "and treasure-land-spawns.sql."),
        // ROUND2: daily and weekly limits.
        Absolute(
            ServerRateKeys.LuckyGodsDaily, DailyCategory, 10,
            "Lucky Gods wishes per day", "wishes", 10m, 0m, 1000m, 1m,
            "Application/LuckyGods/LuckyGodsWishContracts.cs",
            "Wishes one character may make at the Lucky Gods per realm day. " +
            "The start-up step loosens the stock database CHECK (at most 10) " +
            "to 'at least 0' so a higher value can be stored."),
        Absolute(
            ServerRateKeys.WishingPoolDaily, DailyCategory, 20,
            "Wishing Pool uses per day", "uses", 3m, 0m, 1000m, 1m,
            "Infrastructure/WishingPool/PostgresWishingPoolUsageStore.cs",
            "Wishing Pool uses per character per realm day. The 1-hour wait " +
            "between uses is unchanged."),
        Absolute(
            ServerRateKeys.ZeusGiftsWeekday, DailyCategory, 30,
            "Zeus' Gift deliveries per weekday", "gifts", 10m, 0m, 12m, 1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs GiftLimit",
            "Gift deliveries per character on Monday-Friday. 12 is the most " +
            "the gift round table has."),
        Absolute(
            ServerRateKeys.ZeusGiftsWeekend, DailyCategory, 40,
            "Zeus' Gift deliveries per weekend day", "gifts", 12m, 0m, 12m, 1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs GiftLimit",
            "Gift deliveries per character on Saturday and Sunday."),
        Absolute(
            ServerRateKeys.ZeusExchangesPerWeek, DailyCategory, 50,
            "Zeus' Gift base exchanges per week", "exchanges", 5m, 0m, 1000m,
            1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs ExchangesForStones",
            "Dust exchanges every character gets each week before stones are " +
            "deposited (every 5 deposited stones still add one)."),
        Multiplier(
            ServerRateKeys.QuestDailyCompletions, DailyCategory, 60,
            "Repeatable quest daily limit", 1m, 1_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs HasDailyAllowance",
            "How many times a repeatable quest may be completed per day: " +
            "each quest's own daily cap times this value. One-time quests " +
            "stay one-time."),
        Multiplier(
            ServerRateKeys.MovementSpeed, CharacterCategory, 90,
            "Movement speed, all players", 0.1m, 10m,
            "Game/GameSessionRegistry.Operator.cs ApplyOperatorModifiers",
            "How fast every player runs. Reaches players online within 15 " +
            "seconds. Multiplies with mounts, buffs and a GM's /speed."),
        // ROUND2: event windows (server time = the realm calendar).
        Absolute(
            ServerRateKeys.PindusAlwaysOpen, EventCategory, 10,
            "Pindus battlefield always open", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "1 = the Pindus Mountains battlefield can be entered at any time. " +
            "0 = the scheduled windows, which follow battlefield.pindus_igg_times " +
            "(default: Wed 11:00, Sat 21:00, Sun 11:00; at 0 the stock Wed/Fri " +
            "21:00, Sat/Sun 08:00 and 15:00/21:00)."),
        Absolute(
            ServerRateKeys.NiMiniAlwaysOpen, EventCategory, 20,
            "Ni Mini Valley always open", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "1 = Ni Mini Valley can be entered at any time. 0 = the scheduled " +
            "windows, which follow battlefield.nimini_igg_times (default: Fri " +
            "21:45, Sun 19:00; at 0 the stock Fri 19:00, Sat 07:00, Sun 19:00)."),
        Absolute(
            ServerRateKeys.LelantineAlwaysOpen, EventCategory, 25,
            "Lelantine Farm always open", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "1 = the Lelantine Farm can be entered at any time (the old test behaviour). " +
            "0 = only during its round, Saturday 22:45-23:30 Asia/Manila (IGG guide, " +
            "GodsArena wiki and the client's NF_L0_Y7004 text agree on Saturday 22:45-23:30). " +
            "GM accounts on the developer list skip the gate either way."),
        Absolute(
            ServerRateKeys.BattlefieldWindowMinutes, EventCategory, 30,
            "Battlefield entry window length", "minutes", 45m, 5m, 1440m, 5m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "How long each battlefield entry window stays open after its start " +
            "time."),
        Absolute(
            ServerRateKeys.BattlefieldShiftHours, EventCategory, 40,
            "Battlefield start time shift", "hours", 0m, -12m, 12m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "Moves every battlefield start time by this many hours (for " +
            "example -2 makes the 21:00 Pindus window start at 19:00 server " +
            "time = 21:00 AEST). The client's event list still prints the " +
            "stock times."),
        Absolute(
            ServerRateKeys.WonderlandCloseHour, EventCategory, 50,
            "Wonderland closes at", "hour", 23m, 1m, 24m, 1m,
            "Application/WorldInstances/LegacyInstanceAdmissionPolicy.cs",
            "Wonderland can be entered every day until this hour (server " +
            "time). 24 = open all day."),
        Absolute(
            ServerRateKeys.SpyInvasion, EventCategory, 70,
            "Spy invasion waves", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/SpyInvasionPolicy.cs",
            "1 = the spies in Athens and Sparta appear only Monday, " +
            "Wednesday and Saturday at 19:00 server time, in 5-minute waves " +
            "until 19:30. 0 = spies are always there."),
        Absolute( // EVENTSBOARD
            ServerRateKeys.EventBoard, EventCategory, 80,
            "Event window lists", "0/1", 0m, 0m, 1m, 1m,
            "Application/Events/EventBoard.cs",
            "1 = fill the client's Event window (Today / This week) with the " +
            "battlefield, Wonderland, spy and Zeus' Gift times. 0 = empty."),
        // FINALISE (the admin 2026-11-05): bag-open gains in the personal info window.
        Absolute(
            ServerRateKeys.BagGainLog, EventCategory, 110,
            "Bag gains in the info window", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.GainLog.cs",
            "1 = opening an EXP / TP / money / gold / item bag prints one line " +
            "with what it gave in the lower-right info window (Other Info). " +
            "0 = no line (the stock toasts still show)."),
        // FINALISE2 (the admin F-63): kill and pet-EXP lines in the info window (Other / Pet tab on client 1.4.2).
        Absolute(
            ServerRateKeys.KillGainLog, EventCategory, 112,
            "Kill EXP in the info window", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.GainLog.F2.cs",
            "1 = every kill prints one line with the EXP it gave (Other Info). " +
            "0 = no line. Turn off if players find it too busy."),
        Absolute(
            ServerRateKeys.PetGainLog, EventCategory, 113,
            "Pet EXP in the info window", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.GainLog.F2.cs",
            "1 = a kill that gives the active pet EXP prints one line " +
            "(Pet tab on client 1.4.2, Other Info before). 0 = no line."),
        // FINALISE (the admin 2026-11-05): GMs may choose to obey transporter gates.
        Absolute(
            ServerRateKeys.GmObeyGates, EventCategory, 100,
            "GMs obey transporter gates", "0/1", 0m, 0m, 1m, 1m,
            "Game/GameClientHandler.BattlefieldTransporter.cs",
            "0 = GM accounts skip the level and opening-hour gates of the " +
            "battlefield transporter (Pindus, Ni Mini, Lelantine Farm) to test " +
            "or run events. 1 = GMs are refused like any other player."),
        // FINALISE (the admin 2026-11-05): world boss respawn notice.
        Absolute(
            ServerRateKeys.WorldBossNotice, EventCategory, 90,
            "World boss spawn notice", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameSessionRegistry.WorldBossNotice.cs",
            "1 = a centre-screen line when a world boss comes back after a " +
            "kill: Marathon/Parnitha/Parnitha Port bosses to Athenians only, " +
            "Peloponnese/Nemea/Nemea Forest bosses to Spartans only, every " +
            "other world boss to both. 0 = no notice."),
        Absolute(
            ServerRateKeys.ZeusStoneAnyDay, EventCategory, 60,
            "Zeus' Gift stones any day", "0/1", 0m, 0m, 1m, 1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs",
            "1 = praying stones can be handed in on any day, not only " +
            "Monday-Friday (same as the GODSWAR_ZEUS_STONE_ANY_DAY switch)."),
        // CLASSCHANGE: off by default, so shipping the binary changes nothing.
        Absolute(
            ServerRateKeys.ClassChangeEnabled, NpcServiceCategory, 10,
            "Class Shifter changes class", "0/1", 0m, 0m, 1m, 1m,
            "Game/ScriptedNpcDialogueCatalog.ClassChange.cs",
            "1 = the Class Shifter (Athens/Sparta 141,-173) really changes the " +
            "class: nothing may be worn, the old class's skills are sealed, the " +
            "new class's start skills granted and old talents refunded as Talent " +
            "Points; the player is logged out to reload. 0 = menu only (same as " +
            "leaving GODSWAR_CLASS_CHANGE unset)."),
        Absolute(
            ServerRateKeys.ClassChangeFreeLevel, NpcServiceCategory, 20,
            "Class change free up to level", "level", 100m, 0m, 200m, 1m,
            "Game/ScriptedNpcDialogueCatalog.ClassChange.cs",
            "Characters at or below this level change class for free; above it " +
            "one Class Shift Permit (item 4266) is used. Retail: 100."),
        // FINALISE (the admin 2026-11-05): no disconnect after a class shift.
        Absolute(
            ServerRateKeys.ClassChangeInPlaceRefresh, NpcServiceCategory, 30,
            "Class shift refreshes in place", "0/1", 1m, 0m, 1m, 1m,
            "Game/ScriptedNpcDialogueCatalog.ClassChange.cs",
            "1 = after a class shift the server resends the character detail " +
            "(profession), stats, talent ranks, skill list and model, and the " +
            "player stays online (the retail Class Shifter keeps talking after " +
            "\"Class shifted!\"). 0 = the old way: the player is logged out and " +
            "the next login reloads everything. Not yet seen in a real client: " +
            "set 0 if the client shows the old class."),
        // PARTY (r02): the admin's rule - party members near the kill share the
        // kill-quest credit, and the kill EXP is split equally between them.
        Absolute(
            ServerRateKeys.PartyKillShare, PartyCategory, 10,
            "Party kill sharing", "0/1", 0m, 0m, 1m, 1m, // RELEASE: default off (the admin turns it on)
            "Game/GameClientHandler.PartyKillShare.cs",
            "1 = party members on the killer's map, alive and near the kill " +
            "get kill-quest credit and an equal share of the kill EXP and " +
            "Talent EXP (drops stay with the killer). 0 = only the killer, " +
            "as before."),
        Absolute(
            ServerRateKeys.PartyKillShareCells, PartyCategory, 20,
            "Party sharing range (32-unit cells)", "cells", 2m, 0m, 8m, 1m,
            "Game/GameSessionRegistry.PartyKillShare.cs",
            "How near the kill a party member must be, in 32-unit world " +
            "cells around the cell the monster died in (2 = up to two cells " +
            "away in each direction, about 64-95 units; inferred from the " +
            "2008 server's IsTwoCellCross team-EXP test)."),
        // TRADE: off by default. The frame layouts come from the client disassembly and a
        // headless rig test, not from a real client (Development/build/trade/README.md).
        Absolute(
            ServerRateKeys.PlayerTrade, TradeCategory, 10,
            "Player trade window", "0/1", 0m, 0m, 1m, 1m,
            "Game/GameClientHandler.PlayerTrade.cs",
            "1 = the Trade entry of the player menu opens the trade window on both sides " +
            "(items and money, both press OK, one database transaction). 0 = off (stock)."),
        Absolute(
            ServerRateKeys.PlayerStalls, TradeCategory, 20,
            "Player stalls", "0/1", 0m, 0m, 1m, 1m,
            "Game/GameClientHandler.PlayerStall.cs",
            "1 = players can set up a stall from the bag and others can browse and buy. " +
            "0 = off (stock)."),
        // BATTLEFIELD: own category so the round-2 category counts stay as they are.
        Absolute(
            ServerRateKeys.BattlefieldAwarder, BattlefieldCategory, 10,
            "Battlefield Awarder pays rewards", "0/1", 0m, 0m, 1m, 1m, // RELEASE: default off
            "Game/GameClientHandler.BattlefieldAwarder.cs",
            "1 = the capital Battlefield Awarders (Athens_073/Sparta_073) answer " +
            "claims for Pindus, Ni Mini and the Lelantine Farm with the rewards " +
            "the round earned. 0 = the old fixed pages (nothing is paid)."),
        Absolute(
            ServerRateKeys.BattlefieldScoring, BattlefieldCategory, 20,
            "Battlefield kill scoring", "0/1", 0m, 0m, 1m, 1m, // RELEASE: default off
            "Game/GameClientHandler.BattlefieldScoring.cs",
            "1 = enemy kills on Pindus (map 38) and Ni Mini (map 34) score " +
            "inside an open window and entering records participation. 0 = no " +
            "scoring (rounds already settled can still be claimed)."),
        Absolute(
            ServerRateKeys.BattlefieldClaimHours, BattlefieldCategory, 30,
            "Battlefield reward claim time", "hours", 0m, 0m, 720m, 1m,
            "Application/WorldInstances/BattlefieldRewardPolicy.cs",
            "0 = rewards must be claimed on the day of the battle, until 23:55 " +
            "server time (IGG guide). A number = that many hours after the " +
            "battle ends (generated option)."),
        Absolute(
            ServerRateKeys.BattlefieldTitleDays, BattlefieldCategory, 40,
            "Battlefield title days", "days", 7m, 0m, 365m, 1m,
            "State/PostgresGameStore.BattlefieldRounds.cs",
            "Days a Pindus or Ni Mini title lasts after it is claimed (IGG: Ni " +
            "Mini one week; Pindus not stated, generated). 0 = permanent."),
        Multiplier(
            ServerRateKeys.BattlefieldReputation, BattlefieldCategory, 50,
            "Battlefield Reputation", 0m, 100m,
            "Game/GameClientHandler.BattlefieldAwarder.cs",
            "Scales the Reputation the Awarder pays (Pindus 800/400, Ni Mini " +
            "600/200). The client's own text still shows the stock amounts. " +
            "EXP and Talent follow the Event NPC EXP & Talent rate."),
        Absolute(
            ServerRateKeys.BattlefieldPindusKillPoints, BattlefieldCategory, 60,
            "Pindus points per enemy kill", "points", 5m, 0m, 1000m, 1m,
            "Game/GameClientHandler.BattlefieldScoring.cs",
            "Faction points for killing an enemy player on Pindus (IGG guide: " +
            "5). The killer also gets 1 personal point (client text). Ni Mini " +
            "always scores 1 per kill."),
        Absolute(
            ServerRateKeys.BattlefieldNiMiniIggTimes, BattlefieldCategory, 70,
            "Ni Mini at the IGG times", "0/1", 0m, 0m, 1m, 1m, // RELEASE: default off (the admin named only Pindus times)
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "1 = Ni Mini Valley opens Fridays 21:45-22:30 and Sundays " +
            "19:00-19:45 server time (IGG guide). 0 = the stock reborn windows " +
            "(Fri 19:00, Sat 07:00, Sun 19:00). Editor windows override both."),
        // BATTLEFIELD2
        Absolute(
            ServerRateKeys.BattlefieldPindusIggTimes, BattlefieldCategory, 80,
            "Pindus at the IGG times", "0/1", 1m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "1 = Pindus opens Wednesdays 11:00-11:45, Saturdays 21:00-21:45 and " +
            "Sundays 11:00-11:45 server time (IGG guide; the client's own " +
            "transporter text says the same). 0 = the stock reborn windows (Wed/Fri " +
            "21:00, Sat 08:00 and 15:00, Sun 08:00 and 21:00). Editor windows " +
            "override both."),
        Absolute(
            ServerRateKeys.BattlefieldPindusBand, BattlefieldCategory, 90,
            "Pindus level 60-200", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.BattlefieldTransporter.cs",
            "1 = the Battlefield Transporter admits level 60-200 to Pindus (the admin " +
            "2026-11-03; IGG says 60-130) and only that band scores there. 0 = " +
            "the stock reborn band 31-120 and the stock refusal lines."),
        Absolute(
            ServerRateKeys.BattlefieldShowPaidAmounts, BattlefieldCategory, 100,
            "Awarder shows paid amounts", "0/1", 0m, 0m, 1m, 1m, // RELEASE: default off
            "Game/GameClientHandler.BattlefieldAwarder.cs",
            "1 = the Pindus Awarder's EXP and Talent lines print what is paid " +
            "(including the Event NPC EXP & Talent rate) where the client can. " +
            "0 = the base x1 amounts."),
        // MARRIAGE: off by default, so shipping the binary changes nothing.
        Absolute(
            ServerRateKeys.MarriageEnabled, MarriageCategory, 10,
            "Cupid marriages and divorces", "0/1", 0m, 0m, 1m, 1m,
            "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "1 = Cupid, Cupid's Shadow and the Avatar of Pan run engagement, " +
            "ceremony booking, the wedding, the gift and divorce. 0 = menu only " +
            "(same as leaving GODSWAR_MARRIAGE unset). Run " +
            "Development/build/marriage/migration.sql before turning it on."),
        Absolute(
            ServerRateKeys.MarriageNecklaceRequired, MarriageCategory, 20,
            "Whelk Necklace needed to marry", "0/1", 1m, 0m, 1m, 1m,
            "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "1 = retail: booking needs the groom's Whelk Necklace (3934) and the " +
            "ceremony one each. 0 = not needed (Pan's Labyrinth is closed)."),
        Absolute(
            ServerRateKeys.MarriageEngagementSilver, MarriageCategory, 30,
            "Engagement price", "Silver", 9999m, 0m, 100000000m, 1m,
            "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "Silver the groom pays when the bride accepts. Retail: 9999."),
        Absolute(
            ServerRateKeys.MarriageBookingGold, MarriageCategory, 40,
            "Wedding booking price", "Gold", 100m, 0m, 100000m, 1m,
            "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "Gold the groom pays to book a ceremony hour. Retail: 100.")
    ];

    private static readonly FrozenDictionary<string, ServerRateDefinition>
        ByKey = Definitions.ToFrozenDictionary(
            static definition => definition.Key,
            StringComparer.Ordinal);

    public static bool TryGet(
        string key,
        out ServerRateDefinition definition) =>
        ByKey.TryGetValue(key, out definition!);

    public static ServerRateDefinition Get(string key) =>
        ByKey.TryGetValue(key, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "Unknown server rate.");

    private static ServerRateDefinition Multiplier(
        string key,
        string category,
        int sortOrder,
        string label,
        decimal minimum,
        decimal maximum,
        string appliesTo,
        string description) =>
        new(
            key,
            category,
            label,
            "x",
            ServerRateKind.Multiplier,
            1m,
            minimum,
            maximum,
            0.05m,
            sortOrder,
            appliesTo,
            description);

    private static ServerRateDefinition Absolute(
        string key,
        string category,
        int sortOrder,
        string label,
        string unit,
        decimal defaultValue,
        decimal minimum,
        decimal maximum,
        decimal step,
        string appliesTo,
        string description) =>
        new(
            key,
            category,
            label,
            unit,
            ServerRateKind.Absolute,
            defaultValue,
            minimum,
            maximum,
            step,
            sortOrder,
            appliesTo,
            description);
}

/// <summary>
/// One immutable set of rates. Values outside the compiled bounds are clamped
/// when the snapshot is built, so gameplay code never sees an unsafe value.
/// </summary>
internal sealed class ServerRatesSnapshot
{
    private readonly FrozenDictionary<string, decimal> _values;

    private ServerRatesSnapshot(
        FrozenDictionary<string, decimal> values,
        long revision,
        DateTimeOffset updatedAtUtc,
        string updatedBy,
        string source,
        DateTimeOffset loadedAtUtc)
    {
        _values = values;
        Revision = revision;
        UpdatedAtUtc = updatedAtUtc;
        UpdatedBy = updatedBy;
        Source = source;
        LoadedAtUtc = loadedAtUtc;
    }

    public static ServerRatesSnapshot Default { get; } = new(
        ServerRateCatalog.Definitions.ToFrozenDictionary(
            static definition => definition.Key,
            static definition => definition.DefaultValue,
            StringComparer.Ordinal),
        0,
        DateTimeOffset.UnixEpoch,
        "compiled-default",
        "compiled-default",
        DateTimeOffset.UnixEpoch);

    public long Revision { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public string UpdatedBy { get; }

    /// <summary><c>compiled-default</c>, <c>database</c> or <c>test</c>.</summary>
    public string Source { get; }

    public DateTimeOffset LoadedAtUtc { get; }

    public IReadOnlyDictionary<string, decimal> Values => _values;

    public IReadOnlyList<string> NonDefaultKeys =>
        ServerRateCatalog.Definitions
            .Where(definition => Get(definition.Key) != definition.DefaultValue)
            .Select(static definition => definition.Key)
            .ToArray();

    public static ServerRatesSnapshot Create(
        IEnumerable<KeyValuePair<string, decimal>> values,
        long revision,
        DateTimeOffset updatedAtUtc,
        string updatedBy,
        string source,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var merged = ServerRateCatalog.Definitions.ToDictionary(
            static definition => definition.Key,
            static definition => definition.DefaultValue,
            StringComparer.Ordinal);
        foreach (var (key, raw) in values)
        {
            if (!ServerRateCatalog.TryGet(key, out var definition))
            {
                warnings?.Add($"unknown rate '{key}' ignored");
                continue;
            }

            var value = definition.Kind == ServerRateKind.Absolute
                ? decimal.Truncate(raw)
                : raw;
            if (value < definition.MinimumValue ||
                value > definition.MaximumValue)
            {
                var clamped = Math.Clamp(
                    value,
                    definition.MinimumValue,
                    definition.MaximumValue);
                warnings?.Add(
                    $"rate '{key}' = {raw} is outside " +
                    $"{definition.MinimumValue}..{definition.MaximumValue}; " +
                    $"using {clamped}");
                value = clamped;
            }

            merged[key] = value;
        }

        return new ServerRatesSnapshot(
            merged.ToFrozenDictionary(StringComparer.Ordinal),
            Math.Max(0, revision),
            updatedAtUtc,
            string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy,
            string.IsNullOrWhiteSpace(source) ? "unknown" : source,
            DateTimeOffset.UtcNow);
    }

    public decimal Get(string key) =>
        _values.TryGetValue(key, out var value)
            ? value
            : ServerRateCatalog.Get(key).DefaultValue;

    public bool IsDefault(string key) =>
        Get(key) == ServerRateCatalog.Get(key).DefaultValue;

    /// <summary>
    /// Scales a positive stock amount, truncating once and saturating at
    /// <paramref name="maximum"/>. A rate of exactly 1 returns the input
    /// unchanged, so stock behaviour (including stock edge cases) is kept.
    /// </summary>
    public int Multiply(string key, int value, int maximum = int.MaxValue)
    {
        var rate = Get(key);
        if (rate == 1m || value <= 0)
        {
            return value;
        }

        var scaled = decimal.Truncate(value * rate);
        if (scaled <= 0)
        {
            return 0;
        }

        return scaled >= maximum ? maximum : (int)scaled;
    }

    public long Multiply(string key, long value, long maximum = long.MaxValue)
    {
        var rate = Get(key);
        if (rate == 1m || value <= 0)
        {
            return value;
        }

        var scaled = decimal.Truncate(value * rate);
        if (scaled <= 0)
        {
            return 0;
        }

        return scaled >= maximum ? maximum : (long)scaled;
    }

    /// <summary>Scales a 0..100 success chance and keeps it in 0..100.</summary>
    public int MultiplyPercent(string key, int percent)
    {
        var rate = Get(key);
        if (rate == 1m)
        {
            return percent;
        }

        return (int)Math.Clamp(
            decimal.Truncate(Math.Max(0, percent) * rate),
            0m,
            100m);
    }

    /// <summary>Scales a 0..10000 chance in basis points.</summary>
    public int MultiplyBasisPoints(string key, int basisPoints)
    {
        var rate = Get(key);
        if (rate == 1m)
        {
            return basisPoints;
        }

        return (int)Math.Clamp(
            decimal.Truncate(Math.Max(0, basisPoints) * rate),
            0m,
            10_000m);
    }

    /// <summary>An absolute limit as a whole number.</summary>
    public long Absolute(string key) => (long)decimal.Truncate(Get(key));

    // INSTANCES
    public bool IsHarborAttackRestartRefundOn() =>
        Absolute(ServerRateKeys.HarborAttackRestartRefund) == 1;
}

internal static class ServerRates
{
    private static ServerRatesSnapshot _current = ServerRatesSnapshot.Default;

    public static ServerRatesSnapshot Current => Volatile.Read(ref _current);

    public static void Install(ServerRatesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

    public static void ResetToDefaults() => Install(ServerRatesSnapshot.Default);

    /// <summary>
    /// Management hooks registered by the runtime that owns the database
    /// connection, so the loopback management server does not depend on
    /// Infrastructure. Both return a JSON document.
    /// </summary>
    public static Func<string>? DescribeHook { get; set; }

    public static Func<CancellationToken, Task<string>>? ReloadHook { get; set; }
}
