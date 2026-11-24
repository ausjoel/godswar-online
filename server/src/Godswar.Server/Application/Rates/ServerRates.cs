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
    // B21 (the admin 2026-11-21): Training Ground monsters appear once per run and the points target is one full clear.
    public const string TrainingGroundOneClear = "instance.training_one_clear"; // B21
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
    public const string ClassShiftEquipmentEnabled = "npc.class_shift_equipment"; // B22 B27-C
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
            "Multiplies the character EXP of every monster kill. One kill gives at most " +
            "100,000,000 EXP. Monsters far below the player's level still give nothing. Works on " +
            "top of EXP potions and the Global monster EXP multiplier."),
        Multiplier(
            ServerRateKeys.MonsterTalentExperience, CharacterCategory, 20,
            "Monster kill Talent EXP (TP)", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Progression.cs PrepareMonsterKillRewardAsync",
            "Multiplies the Talent EXP of every monster kill. Normally a kill gives 10 Talent " +
            "EXP, and 100 Talent EXP make 1 Talent Point. x2 = a Talent Point every 5 kills " +
            "instead of 10."),
        Multiplier(
            ServerRateKeys.QuestExperience, CharacterCategory, 30,
            "Quest reward EXP", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Multiplies the EXP paid when a quest is handed in. Applied after the +10% quest " +
            "appraisal bonus."),
        Multiplier(
            ServerRateKeys.QuestTalent, CharacterCategory, 40,
            "Quest reward Talent", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Multiplies the Talent paid when a quest is handed in. It is paid as Talent EXP: 100 " +
            "make 1 Talent Point."),
        Multiplier(
            ServerRateKeys.EventExperience, CharacterCategory, 50,
            "Event NPC EXP & Talent", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.LuckyGods.cs, Game/GameClientHandler.ZeusGift.cs",
            "Multiplies the EXP and Talent paid by event NPCs: the Lucky Gods wishes (normally " +
            "210,000 to 13,490,000 EXP per claim) and Zeus' Gift praying stones (8,000 EXP and 1 " +
            "Talent each, doubled at weekends). Seasonal events and battlefield rewards use it " +
            "too."),
        Multiplier(
            ServerRateKeys.PetMonsterExperience, PetCategory, 10,
            "Pet EXP from monster kills", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Progression.cs -> PostgresMonsterRewardExtrasStore",
            "Multiplies the EXP a summoned pet gets from monster kills. x3 = the pet levels three " +
            "times as fast. One kill gives at most 100,000,000 EXP, and pets stop at level 120."),
        Multiplier(
            ServerRateKeys.ZodiacEnergy, ZodiacCategory, 10,
            "Zodiac energy gain", 0m, 1_000_000m, // ROUND2: max
            "State/ZodiacEnergyAccrual.cs AddEnergyX100",
            "Multiplies the zodiac energy earned for time online (normally 10 every 5 minutes, 20 " +
            "during the first 3 hours of each day). Energy is still limited by the store of the " +
            "character's zodiac level."),
        // TITLES: off by default, so shipping the binary changes nothing.
        Absolute(
            ServerRateKeys.ZodiacLuckyDayBonus, ZodiacCategory, 20,
            "Zodiac Lucky Day EXP bonus", "%", 5m, 0m, 100m, 1m,
            "Application/Zodiac/FortuneTellerPolicy.cs ApplyLuckyBonus",
            "Extra EXP and Talent EXP from monster kills, in percent, while a Fortune Teller " +
            "Lucky Day is active (24 hours). The original game gave 5. Used only while 'Fortune " +
            "Teller works' is on."),
        Absolute(
            ServerRateKeys.TitleExpertEnabled, "NPC services", 30,
            "Title Expert grants titles", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Game/ScriptedNpcDialogueCatalog.TitlesFortune.cs",
            "Lets the Title Expert grant titles. On: he grants the levelling and attribute " +
            "titles, and quests that give a title grant it on hand-in. Off: he only shows his " +
            "menu."),
        Absolute(
            ServerRateKeys.FortuneTellerEnabled, "NPC services", 40,
            "Fortune Teller works", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Game/ScriptedNpcDialogueCatalog.TitlesFortune.cs",
            "Makes the Fortune Teller work. On: she changes the zodiac sign (1 free a month) and " +
            "draws Lucky Days (1 free a week, extra EXP for 24 hours). Off: she only shows her " +
            "menu."),
        Absolute(
            ServerRateKeys.FortuneTellerGold, "NPC services", 50,
            "Fortune Teller gold price", "Gold", 230m, 0m, 1_000_000m, 1m,
            "Application/Zodiac/FortuneTellerPolicy.cs",
            "Gold the Fortune Teller charges for an extra sign change or an extra Lucky Day. The " +
            "game's buttons say 230 Gold."),
        Multiplier(
            ServerRateKeys.AtlantisHardPoints, InstanceCategory, 10,
            "Atlantis honour", 0m, 10_000m, // ROUND2: max
            "Application/WorldInstances/AtlantisCompletionRewardContracts.cs",
            "Multiplies the Honour paid when an Atlantis run ends (normally 200 to 2,400 by team " +
            "score, 2,800 for a full clear). Titles are not changed."),
        Multiplier(
            ServerRateKeys.MedusaHonor, InstanceCategory, 20,
            "Medusa Island honour", 0m, 10_000m, // ROUND2: max
            "Application/WorldInstances/MedusaCompletionRewardPolicy.cs",
            "Multiplies the Honour paid when a Medusa Island run ends (normally 300 to 1,350 by " +
            "score and 975 to 2,250 by clear time). Titles are not changed."),
        Multiplier(
            ServerRateKeys.FarmKillPoints, InstanceCategory, 30,
            "Lelantine Farm kill points", 0m, 1_000m, // ROUND2: max
            "Game/GameClientHandler.LelantineFarmKills.cs",
            "Multiplies the points a Lelantine Farm kill scores (normally 1, 10 for an elite, " +
            "1,000 for Cerberus). Egg donations are not changed."),
        // ECONOMY r10. RELEASE: default 0 (off) instead of the Multiplier helper's 1,
        // and sort order 45 (40 is Bay Under Attack entry).
        new ServerRateDefinition(
            ServerRateKeys.WonderlandMedals, InstanceCategory,
            "Wonderland Medals per island", "x", ServerRateKind.Multiplier,
            0m, 0m, 100m, 0.05m, 45,
            "Application/WorldInstances/WonderlandMedalPolicy.cs",
            "Multiplies the Medals every finisher gets when a Wonderland island is cleared (50 to " +
            "720 by island, 1,400 for a full run at x1). x0 = no Medals. Medals are spent at the " +
            "Point Exchanger."),
        // INSTANCES: 0 = closed, so nobody spends a daily entry on an instance
        // that is not finished. The Instance Caller answers its pages with the
        // client's own "remaining today is 0" line plus a note.
        // BAY2: Bay Under Attack is built (the admin 2026-11-03 round 2): OPEN by default.
        Absolute(
            ServerRateKeys.HarborAttackEntry, InstanceCategory, 40,
            "Bay Under Attack entry open", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.InstanceCaller.cs (Instance Caller page 16 / 230)",
            "Opens the Bay Under Attack instance. On: the Instance Caller lets a party of level " +
            "50 to 90 in. Off: he refuses, and no daily entry is used."),
        Absolute(
            ServerRateKeys.TrainingGroundEntry, InstanceCategory, 50,
            "Training Ground entry open", "0/1", 0m, 0m, 1m, 1m,
            "Game/GameClientHandler.InstanceCaller.cs (Instance Caller pages 13, 17 / 231)",
            "Opens the Training Ground (Trials of Hercules). On: the Instance Caller starts the " +
            "run: 30 minutes, once a day (points: see Training Grounds once per run). Off: he refuses. Its monsters must be " +
            "loaded on the server first."),
        // WARRIOR (F-51): closed until the spawns are loaded and the admin opens it.
        Absolute(
            ServerRateKeys.WarriorTrainingEntry, InstanceCategory, 55,
            "Warriors' Training Ground entry open", "0/1", 0m, 0m, 1m, 1m,
            "Game/GameClientHandler.InstanceCaller.cs (Instance Caller root 13 / 200, 201, 202, 301)",
            "Opens the four Warriors' Training Grounds. On: the Instance Caller starts the runs: " +
            "20 minutes, each once a day (points: see Training Grounds once per run). Off: he refuses, and no daily entry is " +
            "used. Their monsters must be loaded on the server first."),
        // B21 (the admin 2026-11-21): one clear per run, ON by default; 0 = the old 10 s respawn and fixed 300/200 points.
        Absolute( // B21
            ServerRateKeys.TrainingGroundOneClear, InstanceCategory, 57,
            "Training Grounds: monsters once per run", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameSessionRegistry.TrainingGround.cs (run start, kill scoring, rewards)",
            "Trials of Hercules and the Warriors' Training Grounds. On: no monster comes back " +
            "during a run, and the points to clear are what one full clear gives (156 on Hercules, " +
            "142 on each Warriors' ground with today's layouts). Rewards scale to that target. " +
            "Off: normal monsters respawn after 10 s and the target is 300 / 200. Applies to " +
            "runs that start after the change."),
        Absolute(
            ServerRateKeys.HarborAttackRestartRefund, InstanceCategory, 60,
            "Bay Under Attack restart refund", "0/1", 1m, 0m, 1m, 1m,
            "Infrastructure/PostgresApplicationDataRuntime.cs (start-up)",
            "Gives back the daily entry of a Bay Under Attack run that a server stop interrupted. " +
            "On: at server start, members of unfinished runs from the last 36 hours get their " +
            "entry back. Off: no refund. A run that already paid its completion EXP is never " +
            "refunded. This is our own rule, not from the original game."),
        // BAY2: GENERATED completion EXP (the guide gives no amount).
        Absolute(
            ServerRateKeys.HarborAttackExperiencePercent, InstanceCategory, 65,
            "Bay Under Attack completion EXP (% of a level)", "%", 100m, 0m, 1_000m, 5m,
            "Game/GameSessionRegistry.HarborAttackRewards.cs (run end)",
            "EXP for finishing Bay Under Attack, as a percent of the EXP the character needs for " +
            "the next level. 100 = a full clear is worth one level; a partial clear pays in " +
            "proportion. Level 200 gets none. Our own value: the original amount is unknown."),
        // VILLAGERS: own category, so the ROUND2 event-category count is unchanged.
        .. Application.Villagers.VillagersRateDefinitions.All,
        .. Application.North.NorthRateDefinitions.All, // FINALISE-NORTH
        Multiplier(
            ServerRateKeys.DropChance, DropCategory, 10,
            "Drop chance", 0m, 1_000_000m, // ROUND2: max
            "Application/World/Content/MonsterLootContentSnapshot.cs, " +
            "Application/WorldInstances/MedusaMonsterContentSnapshot.cs",
            "Multiplies the drop chance of every item monsters can drop. No single item's chance " +
            "goes above 100%."),
        Multiplier(
            ServerRateKeys.DropQuantity, DropCategory, 20,
            "Drop quantity", 1m, 1_000_000m, // ROUND2: max
            "Application/World/Content/MonsterLootContentSnapshot.cs, " +
            "Application/WorldInstances/MedusaMonsterContentSnapshot.cs",
            "Multiplies how many of each dropped item you get. One drop holds at most 255; " +
            "picking it up splits it into stacks."),
        Multiplier(
            ServerRateKeys.DropMaximumItems, DropCategory, 30,
            "Maximum items per kill", 1m, 1_000_000m, // ROUND2: max
            "Application/World/Content/MonsterLootContentSnapshot.cs",
            "Multiplies how many different items one monster kill may drop. Never more than 32, " +
            "or than the monster has items to drop. A corpse still shows at most 8 at a time: the " +
            "best ones are kept."),
        // F-166 (b): the per-corpse total. A SAFETY cap: the client's 10029 parser overflows at 9+ entries.
        Absolute(
            ServerRateKeys.DropCorpseMaximumItems, DropCategory, 35,
            "Maximum drops per corpse (all sources)", "items", 8m, 1m, 8m, 1m,
            "Game/GameSessionRegistry.MonsterLoot.cs, Application/World/Content/CorpseLootLimit.cs",
            "The most drops one corpse can show, counting every source (normal loot, event and " +
            "season drops, Magic Bell). When more drop, the best are kept: higher quality first, " +
            "then rarer, then more valuable. 8 is the highest possible, because the game's loot " +
            "window has 8 slots."),
        // LOOT PAGING (F-166 note "a 16 loot window"): default 1 = off, exactly the single 8-slot window.
        Absolute(
            ServerRateKeys.DropCorpsePages, DropCategory, 36,
            "Loot pages per corpse", "pages", 1m, 1m, 2m, 1m,
            "Game/GameSessionRegistry.MonsterLoot.cs, Game/CorpseLootPages.cs",
            "How many loot windows a corpse can have. 1 = one window of up to 8 drops. 2 = up to " +
            "16 drops: the first 8 show, and once they are all picked up a second window shows " +
            "the rest. Pick All takes one window at a time."),
        // ATTR: random attributes, quality and grade on profile drops.
        Multiplier(
            ServerRateKeys.DropRareAttributes, DropCategory, 40,
            "Rare attribute rolls (3-4 attributes)", 0m, 100m,
            "Application/World/Content/LootRollProfiles.cs",
            "Multiplies how often dropped gear rolls 3 or 4 random attributes. x2 = twice as " +
            "likely, x0 = never. Only affects drops that use a roll profile."),
        Multiplier(
            ServerRateKeys.DropQualityUp, DropCategory, 50,
            "Higher quality on gear drops", 0m, 10m,
            "Application/World/Content/LootRollProfiles.cs",
            "Multiplies the chance that dropped gear rolls a higher quality. Never beyond the " +
            "highest quality the drop allows. Only affects drops that use a roll profile."),
        Multiplier(
            ServerRateKeys.DropGradeUp, DropCategory, 60,
            "Higher grade on gear drops", 0m, 10m,
            "Application/World/Content/LootRollProfiles.cs",
            "Multiplies the chance that dropped gear rolls a higher grade. Never beyond the " +
            "highest grade the drop allows. Only affects drops that use a roll profile."),
        Multiplier(
            ServerRateKeys.QuestSilver, MoneyCategory, 10,
            "Quest reward Silver", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Multiplies the Silver paid when a quest is handed in."),
        Multiplier(
            ServerRateKeys.QuestGold, MoneyCategory, 20,
            "Quest reward Gold", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Multiplies the Gold paid by quests that pay Gold."),
        Multiplier(
            ServerRateKeys.SellPrice, MoneyCategory, 30,
            "NPC sell price", 0m, 5m, // ROUND2: max
            "Infrastructure/Inventory/PostgresCapitalShopPurchaseStore.CapitalShopSale.cs",
            "Multiplies the Silver a capital NPC shop pays for an item (normally 20% of its " +
            "value). The game's price tooltip keeps showing the normal price."),
        Multiplier(
            ServerRateKeys.ForgeSuccess, ForgeCategory, 10,
            "Forge success chance", 0m, 100m, // ROUND2: max
            "State/EquipmentForgeCalculator.cs",
            "Multiplies the success chance of forging equipment with Ruby, Sapphire or Emerald. " +
            "The chance never goes above 100%. The game shows its own odds."),
        Multiplier(
            ServerRateKeys.HolyStoneSuccess, ForgeCategory, 20,
            "Holy stone upgrade success", 0m, 100m, // ROUND2: max
            "State/HolyStoneUpgradePolicy.cs",
            "Multiplies the success chance of a holy stone upgrade (normally 90%, 25% and 10% for " +
            "levels 1-3, 4-6 and 7-9). The chance never goes above 100%."),
        Absolute(
            ServerRateKeys.HolyBoxDailyLimit, HolyBoxCategory, 10,
            "Daily EXP that can be stored", "EXP", 2_000_000_000m, 0m,
            1_000_000_000_000m, 1_000_000m,
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Store.cs, .Quota.cs",
            "EXP one account may put into Holy Boxes each day (server time). An active Battle " +
            "Pass skips the limit. The game's counter stops showing at 214,748,364; the real " +
            "limit still applies."),
        .. Godswar.Server.Application.Seasons.EasterAnniversarySeason.Definitions, // W3 seasons easter/igg
        .. Godswar.Server.Application.EventsNpc.EventsNpcRates.Definitions, // EVENTSNPC
        .. Godswar.Server.Application.Finalise2.Finalise2Rates.Definitions, // FINALISE2
        .. Godswar.Server.Application.OnlineAwards.OnlineAwardOnlineTime.Definitions, // B21 Online Award minutes online
        .. Godswar.Server.Application.Combat.AreaSkillBalance.Definitions, // B21 item 18 area skill balance
        .. Godswar.Server.Application.Combat.ClassFieldSkills.Definitions, // B22 B23-F class field skill, PvP share
        Godswar.Server.Application.Limits.GameLimitProfile.Definition, // W4F original/Origin limits toggle
        Godswar.Server.Application.Limits.EndgameSwitch.Definition, // W16 end-game master switch (d129)
        .. Godswar.Server.Application.Troy.TroyEvent.Definitions, // W2 F-27 TROY
        .. Godswar.Server.Application.Troy.TroyBossChain.Definitions, // W2 F-27 TROY slice 2: heroes and chests
        .. Godswar.Server.Application.WorldInstances.WonderlandIslandScale.Definitions, // WL-SLIDERS: per-island HP/damage %
        .. Godswar.Server.Application.WorldInstances.AetherSanctumBossScale.Definitions, // B23-S: Sanctum boss HP/damage %
        .. Godswar.Server.Application.WorldInstances.AtlantisRankScale.Definitions, // ATL-SLIDERS: Atlantis per-rank HP/damage %
        .. Godswar.Server.Application.Seasons.MidAutumnSeason.Definitions, // W3 seasons mid-autumn
        Absolute(
            ServerRateKeys.HolyBoxPerStoreMaximum, HolyBoxCategory, 20,
            "Maximum EXP per Store action", "EXP", 400_000_000m, 1m,
            2_147_483_647m, 1_000_000m,
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Store.cs",
            "Largest amount of EXP one Store action may move into a Holy Box."),
        .. Godswar.Server.Application.Seasons.HalloweenMayDaySeason.Definitions, // W3 seasons halloween/may day
        Multiplier(
            ServerRateKeys.HolyBoxCapacity, HolyBoxCategory, 30,
            "Holy Box capacity", 0.1m, 1_000_000m, // ROUND2: max
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Store.cs",
            "Multiplies how much EXP every Holy Box holds (normally 100,000 for Box I up to " +
            "400,000,000 for Box V). No box can hold more than 2,147,483,647, which Box V reaches " +
            "at about x5."),
        Absolute(
            ServerRateKeys.HolyBoxMinimumLevel, HolyBoxCategory, 40,
            "Minimum level (all 4 forger pages)", "level", 70m, 1m, 200m, 1m,
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Planning.cs",
            "Character level needed to use the Master Vestment Forger's four EXP pages. The gear " +
            "itself still needs level 70."),
        // ROUND2: upgrade costs (0 = free).
        Multiplier(
            ServerRateKeys.TalentUpgradeCost, CostCategory, 10,
            "Talent upgrade cost", 0m, 1_000m, // ROUND2: max
            "Application/Talents/TalentProgression.cs CalculateUpgradeCost",
            "Multiplies the Talent Points each talent rank costs. x0.5 = half price, x0 = free. " +
            "The game's talent window keeps showing the normal price."),
        Multiplier(
            ServerRateKeys.ZodiacLevelCost, CostCategory, 20,
            "Zodiac level-up energy cost", 0m, 1_000m, // ROUND2: max
            "State/ZodiacLevelUpgrade.cs ZodiacLevelUpgradeCatalog",
            "Multiplies the zodiac energy each zodiac level costs. x0 = free. The game's zodiac " +
            "window keeps showing the normal cost."),
        Multiplier(
            ServerRateKeys.ZodiacGridCost, CostCategory, 30,
            "Zodiac grid upgrade cost", 0m, 1_000m, // ROUND2: max
            "State/ZodiacSkillGridUpgrade.cs ZodiacSkillGridUpgradeCatalog",
            "Multiplies the zodiac energy and Talent Points each zodiac grid level costs. x0 = " +
            "free. The game's tooltip keeps showing the normal cost."),
        // ROUND2: other EXP sources.
        Multiplier(
            ServerRateKeys.FactionCrierExperience, CharacterCategory, 60,
            "Faction Crier EXP", 0m, 1_000_000m, // ROUND2: max
            "Application/FactionCrier/FactionCrierRewardPolicy.cs",
            "Multiplies the EXP paid when nameplates are exchanged at the Faction Crier."),
        Absolute( // B21 item 15
            Godswar.Server.Application.FactionCrier.FactionCrierRewardPolicy.SilverPerGoldKey, CharacterCategory, 75,
            "Faction Crier: silver per Gold for the Gold prices", "silver", 0m, 0m, 1_000_000m, 100m, // B22 W11 d24: default 0 (was 1,500)
            "Application/FactionCrier/FactionCrierRewardPolicy.cs",
            "Prices the Faction Crier's Gold and B-Gold choices by their multiplier, at the same rate per X as " +
            "the player's own silver choice. 1,500 = 1,000 Gold costs what 1,500,000 silver costs: at level " +
            "131 and above 9X costs 140 Gold, 12X 187, 18X 400 and 24X 533; at level 20 to 39, 15, 20, 40 and " +
            "53. 0 = the fixed prices 312, 459, 936 and 1,377 Gold at every level (the default: the admin wants the " +
            "Gold choices dearer, never cheaper; any value above 0 makes them cheaper)."), // B22
        Multiplier(
            ServerRateKeys.FactionCrierTalent, CharacterCategory, 70,
            "Faction Crier Talent Points", 0m, 1_000_000m, // ROUND2: max
            "Application/FactionCrier/FactionCrierRewardPolicy.cs",
            "Multiplies the Talent Points paid when nameplates are exchanged at the Faction " +
            "Crier."),
        Multiplier(
            ServerRateKeys.ExperiencePill, CharacterCategory, 80,
            "EXP per Exp Pill", 0.05m, 1_000_000m, // ROUND2: max
            "State/PlayerExperienceItemPolicy.cs",
            "Multiplies the EXP one Exp Pill gives (normally 1,000,000). One pill gives at most " +
            "100,000,000 EXP. The old EXP scrolls do nothing on this server and have no setting."),
        Multiplier(
            ServerRateKeys.PetExperienceItems, PetCategory, 20,
            "Pet EXP items (Morning Dew)", 0.05m, 1_000_000m, // ROUND2: max
            "Infrastructure/Pets/PostgresPetDurableCommandExecutor.ExperienceItem.cs",
            "Multiplies the EXP a Morning Dew gives the summoned pet. A pet's EXP cannot go above " +
            "4,294,967,295."),
        Absolute( // petcapture
            ServerRateKeys.WildPetCapture, PetCategory, 30,
            "Wild baby pet capture", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Application/WorldInstances/WildPetCapturePolicy.cs",
            "Lets players catch wild baby pets. On: baby Flower Pixies and Minotaurs on the " +
            "suburb and nearby maps can be caught with the capture tools and the Mysterious Tuck " +
            "Net. Off: they cannot be caught."),
        Absolute( // PETNET: Treasure Land net capture
            ServerRateKeys.TreasureLandCapture, PetCategory, 40,
            "Treasure Land net capture", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Application/WorldInstances/TreasureLandCapturePolicy.cs",
            "Lets players net Treasure Land monsters. On: they can be caught with the capture " +
            "tools and the Mysterious Tuck Net for a Simple, Greater or Superior Travel Pack. " +
            "Off: they cannot be caught. The Treasure Land monsters must be loaded on the server " +
            "first."),
        // ROUND2: daily and weekly limits.
        Absolute(
            ServerRateKeys.LuckyGodsDaily, DailyCategory, 10,
            "Lucky Gods wishes per day", "wishes", 10m, 0m, 1000m, 1m,
            "Application/LuckyGods/LuckyGodsWishContracts.cs",
            "Wishes one character may make at the Lucky Gods each day (server time). The original " +
            "game allowed 10."),
        Absolute(
            ServerRateKeys.WishingPoolDaily, DailyCategory, 20,
            "Wishing Pool uses per day", "uses", 3m, 0m, 1000m, 1m,
            "Infrastructure/WishingPool/PostgresWishingPoolUsageStore.cs",
            "Wishing Pool uses per character each day (server time). The 1-hour wait between uses " +
            "stays."),
        Absolute(
            ServerRateKeys.ZeusGiftsWeekday, DailyCategory, 30,
            "Zeus' Gift deliveries per weekday", "gifts", 10m, 0m, 12m, 1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs GiftLimit",
            "Zeus' Gift deliveries per character on each day from Monday to Friday. 12 is the " +
            "most the game has rounds for."),
        Absolute(
            ServerRateKeys.ZeusGiftsWeekend, DailyCategory, 40,
            "Zeus' Gift deliveries per weekend day", "gifts", 12m, 0m, 12m, 1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs GiftLimit",
            "Zeus' Gift deliveries per character on Saturday and on Sunday. 12 is the most the " +
            "game has rounds for."),
        Absolute(
            ServerRateKeys.ZeusExchangesPerWeek, DailyCategory, 50,
            "Zeus' Gift base exchanges per week", "exchanges", 5m, 0m, 1000m,
            1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs ExchangesForStones",
            "Dust exchanges every character gets each week at Zeus' Gift before depositing " +
            "stones. Every 5 deposited stones still add one more."),
        Multiplier(
            ServerRateKeys.QuestDailyCompletions, DailyCategory, 60,
            "Repeatable quest daily limit", 1m, 1_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs HasDailyAllowance",
            "Multiplies how often a repeatable quest may be completed each day. x2 = twice each " +
            "quest's own daily limit. One-time quests stay one-time."),
        Multiplier(
            ServerRateKeys.MovementSpeed, CharacterCategory, 90,
            "Movement speed, all players", 0.1m, 10m,
            "Game/GameSessionRegistry.Operator.cs ApplyOperatorModifiers",
            "How fast every player runs. Reaches online players within 15 seconds. Multiplies " +
            "with mounts, buffs and a GM's /speed."),
        // ROUND2: event windows (server time = the realm calendar).
        Absolute(
            ServerRateKeys.PindusAlwaysOpen, EventCategory, 10,
            "Pindus battlefield always open", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "Opens the Pindus battlefield at all hours. On: it can be entered at any time. Off: " +
            "only at its scheduled times (see 'Pindus at the IGG times')."),
        Absolute(
            ServerRateKeys.NiMiniAlwaysOpen, EventCategory, 20,
            "Ni Mini Valley always open", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "Opens Ni Mini Valley at all hours. On: it can be entered at any time. Off: only at " +
            "its scheduled times (see 'Ni Mini at the IGG times')."),
        Absolute(
            ServerRateKeys.LelantineAlwaysOpen, EventCategory, 25,
            "Lelantine Farm always open", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "Opens the Lelantine Farm at all hours. On: it can be entered at any time. Off: only " +
            "during its round, Saturday 22:45-23:30 server time, as in the original game. GM " +
            "accounts can always enter."),
        Absolute(
            ServerRateKeys.BattlefieldWindowMinutes, EventCategory, 30,
            "Battlefield entry window length", "minutes", 45m, 5m, 1440m, 5m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "How long each battlefield stays open for entry after its start time, in minutes."),
        Absolute(
            ServerRateKeys.BattlefieldShiftHours, EventCategory, 40,
            "Battlefield start time shift", "hours", 0m, -12m, 12m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "Moves every battlefield start time by this many hours. Example: -2 makes the 21:00 " +
            "Pindus battle start at 19:00 server time. The game's own event list keeps showing " +
            "the normal times."),
        Absolute(
            ServerRateKeys.WonderlandCloseHour, EventCategory, 50,
            "Wonderland closes at", "hour", 23m, 1m, 24m, 1m,
            "Application/WorldInstances/LegacyInstanceAdmissionPolicy.cs",
            "Wonderland can be entered every day until this hour (server time). 24 = open all " +
            "day."),
        Absolute(
            ServerRateKeys.SpyInvasion, EventCategory, 70,
            "Spy invasion waves", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Application/WorldInstances/SpyInvasionPolicy.cs",
            "Makes the spies in Athens and Sparta come in waves. On: they appear only on Monday, " +
            "Wednesday and Saturday at 19:00 server time, in 5-minute waves until 19:30. Off: the " +
            "spies are always there."),
        Absolute( // EVENTSBOARD
            ServerRateKeys.EventBoard, EventCategory, 80,
            "Event window lists", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Application/Events/EventBoard.cs",
            "Fills the game's Event window. On: its Today and This week lists show the " +
            "battlefield, Wonderland, spy and Zeus' Gift times. Off: the lists stay empty."),
        // FINALISE (the admin 2026-11-05): bag-open gains in the personal info window.
        Absolute(
            ServerRateKeys.BagGainLog, EventCategory, 110,
            "Bag gains in the info window", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.GainLog.cs",
            "Shows what a reward bag gave. On: opening an EXP, Talent, money, gold or item bag " +
            "writes one line in the info window (bottom right). Off: no line."),
        // FINALISE2 (the admin F-63): kill and pet-EXP lines in the info window (Other / Pet tab on client 1.4.2).
        Absolute(
            ServerRateKeys.KillGainLog, EventCategory, 112,
            "Kill EXP in the info window", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.GainLog.F2.cs",
            "Shows the EXP of each kill. On: every kill writes one line in the info window " +
            "(bottom right). Off: no line. Turn it off if players find it too busy."),
        Absolute(
            ServerRateKeys.PetGainLog, EventCategory, 113,
            "Pet EXP in the info window", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.GainLog.F2.cs",
            "Shows the pet's EXP from kills. On: a kill that gives the active pet EXP writes one " +
            "line in the info window. Off: no line."),
        // FINALISE (the admin 2026-11-05): GMs may choose to obey transporter gates.
        Absolute(
            ServerRateKeys.GmObeyGates, EventCategory, 100,
            "GMs obey transporter gates", "0/1", 0m, 0m, 1m, 1m,
            "Game/GameClientHandler.BattlefieldTransporter.cs",
            "Makes GM accounts follow the battlefield entry rules. On: GMs are refused like any " +
            "player outside the level range or opening hours. Off: GMs can always enter Pindus, " +
            "Ni Mini Valley and the Lelantine Farm, to test or run events."),
        // FINALISE (the admin 2026-11-05): world boss respawn notice.
        Absolute(
            ServerRateKeys.WorldBossNotice, EventCategory, 90,
            "World boss spawn notice", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameSessionRegistry.WorldBossNotice.cs",
            "Announces a world boss coming back after a kill. On: a line in the middle of the " +
            "screen, sent to the faction whose maps the boss is on (both factions for shared " +
            "maps). Off: no notice."),
        Absolute(
            ServerRateKeys.ZeusStoneAnyDay, EventCategory, 60,
            "Zeus' Gift stones any day", "0/1", 0m, 0m, 1m, 1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs",
            "Lets praying stones be handed in at Zeus' Gift on any day. On: every day of the " +
            "week. Off: Monday to Friday only."),
        // CLASSCHANGE: off by default, so shipping the binary changes nothing.
        Absolute(
            ServerRateKeys.ClassChangeEnabled, NpcServiceCategory, 10,
            "Class Shifter changes class", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Game/ScriptedNpcDialogueCatalog.ClassChange.cs",
            "Lets the Class Shifter really change a character's class. On: with nothing worn, the " +
            "old class's skills are locked away, the new class's starting skills are given and " +
            "spent talents come back as Talent Points. Off: he only shows his menu."),
        Absolute(
            ServerRateKeys.ClassChangeFreeLevel, NpcServiceCategory, 20,
            "Class change free up to level", "level", 100m, 0m, 200m, 1m,
            "Game/ScriptedNpcDialogueCatalog.ClassChange.cs",
            "Characters at or below this level change class for free. Above it, one Class Shift " +
            "Permit is used. The original game used 100."),
        // FINALISE (the admin 2026-11-05): no disconnect after a class shift.
        Absolute(
            ServerRateKeys.ClassChangeInPlaceRefresh, NpcServiceCategory, 30,
            "Class shift refreshes in place", "0/1", 1m, 0m, 1m, 1m,
            "Game/ScriptedNpcDialogueCatalog.ClassChange.cs",
            "Keeps a player online after a class change. On: the server sends the new class, " +
            "stats, talents, skills and look at once. Off: the player is logged out and sees the " +
            "change at the next login. Not yet tested with the real game: turn it off if the old " +
            "class still shows."),
        // B22 B27-C (the admin d136): the weapon and armour shift. On by default: the release is the dll; off = the old menu.
        Absolute(
            ServerRateKeys.ClassShiftEquipmentEnabled, NpcServiceCategory, 40,
            "Class Shifter shifts equipment", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.EquipmentShift.cs",
            "Lets the Class Shifter shift equipment. On: a weapon becomes the same-level weapon of the " +
            "chosen class, armour keeps its name, attack and spell bonuses swap over, and the item is bound. " +
            "Above the class change free level each shift uses one Equipment Shift Permit. Class Suit items " +
            "cannot be shifted. Off: he only shows his menu."),
        // PARTY (r02): the admin's rule - party members near the kill share the
        // kill-quest credit, and the kill EXP is split equally between them.
        Absolute(
            ServerRateKeys.PartyKillShare, PartyCategory, 10,
            "Party kill sharing", "0/1", 0m, 0m, 1m, 1m, // RELEASE: default off (the admin turns it on)
            "Game/GameClientHandler.PartyKillShare.cs",
            "Shares kills with the party. On: party members who are alive, on the same map and " +
            "near the kill get quest credit and an equal share of the EXP and Talent EXP. Off: " +
            "only the killer gets them. Drops always stay with the killer."),
        Absolute(
            ServerRateKeys.PartyKillShareCells, PartyCategory, 20,
            "Party sharing range (32-unit cells)", "cells", 2m, 0m, 8m, 1m,
            "Game/GameSessionRegistry.PartyKillShare.cs",
            "How near the kill a party member must be to share it, in map cells of 32 units. 2 = " +
            "up to two cells away in each direction, about 64 to 95 units."),
        // TRADE: off by default. The frame layouts come from the client disassembly and a
        // headless rig test, not from a real client (Development/build/trade/README.md).
        Absolute(
            ServerRateKeys.PlayerTrade, TradeCategory, 10,
            "Player trade window", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Game/GameClientHandler.PlayerTrade.cs",
            "Makes player-to-player trading work. On: Trade in the player menu opens the trade " +
            "window for both players, for items and money; both must press OK. Off: trading is " +
            "refused."),
        Absolute(
            ServerRateKeys.PlayerStalls, TradeCategory, 20,
            "Player stalls", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Game/GameClientHandler.PlayerStall.cs",
            "Makes player stalls work. On: players can set up a stall from the bag, and others " +
            "can browse it and buy. Off: stalls are refused."),
        // BATTLEFIELD: own category so the round-2 category counts stay as they are.
        Absolute(
            ServerRateKeys.BattlefieldAwarder, BattlefieldCategory, 10,
            "Battlefield Awarder pays rewards", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on) // RELEASE: default off
            "Game/GameClientHandler.BattlefieldAwarder.cs",
            "Lets the Battlefield Awarders in both capitals pay out battlefield rewards. On: " +
            "players claim what their round earned on Pindus, Ni Mini Valley and the Lelantine " +
            "Farm. Off: the Awarders only show their pages and pay nothing."),
        Absolute(
            ServerRateKeys.BattlefieldScoring, BattlefieldCategory, 20,
            "Battlefield kill scoring", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on) // RELEASE: default off
            "Game/GameClientHandler.BattlefieldScoring.cs",
            "Counts enemy kills on Pindus and Ni Mini Valley while a battle is open. On: kills " +
            "score and entering counts as taking part. Off: nothing scores; rounds already " +
            "finished can still be claimed."),
        Absolute(
            ServerRateKeys.BattlefieldClaimHours, BattlefieldCategory, 30,
            "Battlefield reward claim time", "hours", 0m, 0m, 720m, 1m,
            "Application/WorldInstances/BattlefieldRewardPolicy.cs",
            "How long players have to claim a battlefield reward. 0 = until 23:55 on the day of " +
            "the battle, as in the original game. Any other number = that many hours after the " +
            "battle ends."),
        Absolute(
            ServerRateKeys.BattlefieldTitleDays, BattlefieldCategory, 40,
            "Battlefield title days", "days", 7m, 0m, 365m, 1m,
            "State/PostgresGameStore.BattlefieldRounds.cs",
            "Days a Pindus or Ni Mini Valley title lasts after it is claimed. 0 = permanent. The " +
            "original game kept Ni Mini titles for one week."),
        Multiplier(
            ServerRateKeys.BattlefieldReputation, BattlefieldCategory, 50,
            "Battlefield Reputation", 0m, 100m,
            "Game/GameClientHandler.BattlefieldAwarder.cs",
            "Multiplies the Reputation the Battlefield Awarder pays (normally Pindus 800 or 400, " +
            "Ni Mini Valley 600 or 200). The game's own text keeps showing the normal amounts. " +
            "EXP and Talent follow the 'Event NPC EXP & Talent' setting instead."),
        Absolute(
            ServerRateKeys.BattlefieldPindusKillPoints, BattlefieldCategory, 60,
            "Pindus points per enemy kill", "points", 5m, 0m, 1000m, 1m,
            "Game/GameClientHandler.BattlefieldScoring.cs",
            "Faction points for killing an enemy player on Pindus. The original game gave 5. The " +
            "killer also gets 1 personal point; Ni Mini Valley always gives 1 point per kill."),
        Absolute(
            ServerRateKeys.BattlefieldNiMiniIggTimes, BattlefieldCategory, 70,
            "Ni Mini at the IGG times", "0/1", 0m, 0m, 1m, 1m, // RELEASE: default off (the admin named only Pindus times)
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "Chooses the Ni Mini Valley opening times. On: Friday 21:45-22:30 and Sunday " +
            "19:00-19:45 server time, as in the original game. Off: Friday 19:00, Saturday 07:00 " +
            "and Sunday 19:00. Times set on the Gates page replace both."),
        // BATTLEFIELD2
        Absolute(
            ServerRateKeys.BattlefieldPindusIggTimes, BattlefieldCategory, 80,
            "Pindus at the IGG times", "0/1", 1m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "Chooses the Pindus opening times. On: Wednesday 11:00-11:45, Saturday 21:00-21:45 " +
            "and Sunday 11:00-11:45 server time, as in the original game. Off: Wednesday and " +
            "Friday 21:00, Saturday 08:00 and 15:00, Sunday 08:00 and 21:00. Times set on the " +
            "Gates page replace both."),
        Absolute(
            ServerRateKeys.BattlefieldPindusBand, BattlefieldCategory, 90,
            "Pindus level 60-200", "0/1", 1m, 0m, 1m, 1m,
            "Game/GameClientHandler.BattlefieldTransporter.cs",
            "Chooses who may enter Pindus. On: levels 60 to 200, and only they score there. Off: " +
            "levels 31 to 120."),
        Absolute(
            ServerRateKeys.BattlefieldShowPaidAmounts, BattlefieldCategory, 100,
            "Awarder shows paid amounts", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on) // RELEASE: default off
            "Game/GameClientHandler.BattlefieldAwarder.cs",
            "Makes the Pindus Awarder show the real EXP and Talent amounts. On: its lines show " +
            "what is actually paid, including the 'Event NPC EXP & Talent' setting. Off: they " +
            "show the normal x1 amounts."),
        // MARRIAGE: off by default, so shipping the binary changes nothing.
        Absolute(
            ServerRateKeys.MarriageEnabled, MarriageCategory, 10,
            "Cupid marriages and divorces", "0/1", 1m, 0m, 1m, 1m, // B21 item 17: default on (live is on)
            "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "Runs marriages. On: Cupid, Cupid's Shadow and the Avatar of Pan handle engagement, " +
            "booking a ceremony, the wedding, the gift and divorce. Off: they only show their " +
            "menus. The marriage tables must be set up on the server before turning it on."),
        Absolute(
            ServerRateKeys.MarriageNecklaceRequired, MarriageCategory, 20,
            "Whelk Necklace needed to marry", "0/1", 1m, 0m, 1m, 1m,
            "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "Chooses whether a Whelk Necklace is needed to marry. On: the groom needs one to book " +
            "and each partner needs one for the ceremony, as in the original game. Off: no " +
            "necklace is needed."),
        Absolute(
            ServerRateKeys.MarriageEngagementSilver, MarriageCategory, 30,
            "Engagement price", "Silver", 9999m, 0m, 100000000m, 1m,
            "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "Silver the groom pays when the bride accepts the engagement. The original game " +
            "charged 9,999."),
        Absolute(
            ServerRateKeys.MarriageBookingGold, MarriageCategory, 40,
            "Wedding booking price", "Gold", 100m, 0m, 100000m, 1m,
            "Game/ScriptedNpcDialogueCatalog.Marriage.cs",
            "Gold the groom pays to book a ceremony hour. The original game charged 100.")
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
