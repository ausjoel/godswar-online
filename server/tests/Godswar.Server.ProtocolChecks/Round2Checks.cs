using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.Talents;
using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Application.ZeusGift;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// ROUND2: checks for the round-2 patch (GodsWar-2.51/Polish): chat command
// word boundaries, the new server_rates keys (stock at their defaults), the
// event windows, and GM placement of Origin client NPC templates.
internal static class Round2Checks
{
    public const string CheckName =
        "ROUND2 command boundaries, new rates and event windows";

    public static Task RunAsync()
    {
        try
        {
            CheckCommandBoundaries();
            CheckCatalogDefaults();
            CheckCosts();
            CheckDailyCaps();
            CheckEventWindows();
            CheckOriginTemplatePlacement();
            CheckHighRates();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void CheckCommandBoundaries()
    {
        static bool Item(string text) =>
            DeveloperItemCommand.TryParse(text, out _, out _);
        static bool Progression(string text) =>
            DeveloperProgressionChatCommand.TryParse(text, out _, out _);

        Check.True(Item("/item add crystal1 2"), "a bare /item line is a command");
        Check.True(Item("Hero:/item add crystal1 2"), "an /item line after the speaker prefix is a command");
        Check.True(Item("test2:/****** add crystal1 99"), "the masked /gmitem spelling is still a command");
        Check.True(!Item("Gmplay:selling /items cheap"), "'selling /items cheap' is ordinary chat");
        Check.True(!Item("Gmplay:/items for sale"), "'/items' is not the /item command");
        Check.True(!Item("Gmplay:buy my /item now"), "/item in the middle of a line is ordinary chat");
        Check.True(!Item("Gmplay:/rubyred"), "'/rubyred' is not the /ruby command");
        Check.True(Progression("Hero:/level set 200"), "/level after the speaker prefix is a command");
        Check.True(!Progression("Gmplay:I am /level 5 now"), "'I am /level 5 now' is ordinary chat");
        Check.True(!Progression("Gmplay:/leveling add 1"), "'/leveling' is not the /level command");
        Check.True(Progression("Hero:/talentpoints add 5"), "/talentpoints is still recognised");
        Check.True(
            GameClientHandler.TryReadDeveloperMedusaTitleCommand("Hero:/medusatitle test", out _),
            "/medusatitle at the start is a command");
        Check.True(
            !GameClientHandler.TryReadDeveloperMedusaTitleCommand("Gmplay:who has /medusatitle?", out _),
            "/medusatitle in the middle of a line is ordinary chat");

        var text = "Hero:/level set 50 @Bob";
        Check.True(
            GameClientHandler.ExtractOperatorTargetSuffix(ref text) == "Bob" &&
                text == "Hero:/level set 50",
            "a trailing @Name names the target and is stripped");
        var plain = "Hero:/level set 50";
        Check.True(
            GameClientHandler.ExtractOperatorTargetSuffix(ref plain) is null &&
                plain == "Hero:/level set 50",
            "a line without @Name is unchanged");
    }

    private static void CheckCatalogDefaults()
    {
        ServerRates.ResetToDefaults();
        var rates = ServerRates.Current;
        Check.Equal(10L, rates.Absolute(ServerRateKeys.LuckyGodsDaily), "Lucky Gods default is the stock 10");
        Check.Equal(3L, rates.Absolute(ServerRateKeys.WishingPoolDaily), "Wishing Pool default is the stock 3");
        Check.Equal(10L, rates.Absolute(ServerRateKeys.ZeusGiftsWeekday), "Zeus weekday default is the stock 10");
        Check.Equal(12L, rates.Absolute(ServerRateKeys.ZeusGiftsWeekend), "Zeus weekend default is the stock 12");
        Check.Equal(5L, rates.Absolute(ServerRateKeys.ZeusExchangesPerWeek), "Zeus exchanges default is the stock 5");
        Check.Equal(45L, rates.Absolute(ServerRateKeys.BattlefieldWindowMinutes), "battlefield window default is 45 minutes");
        Check.Equal(23L, rates.Absolute(ServerRateKeys.WonderlandCloseHour), "Wonderland closes at 23 by default");
        Check.True(
            ServerRateCatalog.Definitions.Count(definition =>
                definition.Category is ServerRateCatalog.CostCategory or
                    ServerRateCatalog.DailyCategory or
                    ServerRateCatalog.EventCategory &&
                definition.Key != ServerRateKeys.EventBoard && // EVENTSBOARD adds its own switch
                definition.Key is not (ServerRateKeys.WorldBossNotice or ServerRateKeys.GmObeyGates or
                    ServerRateKeys.BagGainLog or // FINALISE adds three switches of its own
                    ServerRateKeys.KillGainLog or ServerRateKeys.PetGainLog or // FINALISE2 (F-63)
                    ServerRateKeys.LelantineAlwaysOpen)) == 16, // FINALISE-PETS adds its own switch
            "round 2 adds 16 cost, limit and event settings");
    }

    private static void CheckCosts()
    {
        ServerRates.ResetToDefaults();
        var stockTalent = Enumerable.Range(0, 100).Select(TalentProgression.CalculateUpgradeCost).ToArray();
        Check.True(
            stockTalent.SequenceEqual(Enumerable.Range(0, 100).Select(TalentProgression.CalculateStockUpgradeCost)),
            "talent costs at rate 1 are the stock costs");
        Check.True(
            ZodiacLevelUpgradeCatalog.TryGetRequirement(1, out var stockLevel) &&
                stockLevel.EnergyCost == 500,
            "zodiac level 1->2 costs the stock 500 energy");
        Check.True(
            ZodiacSkillGridUpgradeCatalog.TryGetRequirement(1, out var stockGrid),
            "zodiac grid level 1 has a requirement");

        Install((ServerRateKeys.TalentUpgradeCost, 0.5m),
            (ServerRateKeys.ZodiacLevelCost, 2m),
            (ServerRateKeys.ZodiacGridCost, 3m));
        Check.Equal(
            TalentProgression.CalculateStockUpgradeCost(50) / 2,
            TalentProgression.CalculateUpgradeCost(50),
            "cost.talent 0.5 halves a talent rank");
        Check.True(
            ZodiacLevelUpgradeCatalog.TryGetRequirement(1, out var doubled) && doubled.EnergyCost == 1000,
            "cost.zodiac_level 2 doubles the zodiac level cost");
        Check.True(
            ZodiacSkillGridUpgradeCatalog.TryGetRequirement(1, out var tripled) &&
                tripled.EnergyCost == stockGrid.EnergyCost * 3 &&
                tripled.TalentPointCost == stockGrid.TalentPointCost * 3,
            "cost.zodiac_grid 3 triples grid energy and Talent Points");

        Install((ServerRateKeys.TalentUpgradeCost, 0m));
        Check.Equal(0, TalentProgression.CalculateUpgradeCost(10), "cost.talent 0 makes ranks free");
        ServerRates.ResetToDefaults();

        Check.True(
            PlayerExperienceItemPolicy.TryApply(10, 0, false, out var stockPill) &&
                stockPill.ExperienceGained == 1_000_000,
            "the EXP pill gives the stock 1,000,000 at rate 1");
        Install((ServerRateKeys.ExperiencePill, 3m));
        Check.True(
            PlayerExperienceItemPolicy.TryApply(10, 0, false, out var ratedPill) &&
                ratedPill.ExperienceGained == 3_000_000,
            "exp.pill 3 gives 3,000,000");
        ServerRates.ResetToDefaults();
    }

    private static void CheckDailyCaps()
    {
        ServerRates.ResetToDefaults();
        var weekday = new DateOnly(2026, 10, 28);
        var saturday = new DateOnly(2026, 10, 31);
        Check.Equal(10, ZeusGiftPolicy.GiftLimit(weekday), "Zeus weekday deliveries are 10 at stock");
        Check.Equal(12, ZeusGiftPolicy.GiftLimit(saturday), "Zeus weekend deliveries are 12 at stock");
        Check.Equal(5, ZeusGiftPolicy.ExchangesForStones(0), "Zeus exchanges are 5 at stock");
        Check.Equal(7, ZeusGiftPolicy.ExchangesForStones(10), "every 5 stones still add an exchange");
        Check.True(!ZeusGiftPolicy.StonesAcceptedEveryDay, "stones keep the weekday rule at stock");

        Install((ServerRateKeys.ZeusGiftsWeekday, 12m),
            (ServerRateKeys.ZeusExchangesPerWeek, 20m),
            (ServerRateKeys.ZeusStoneAnyDay, 1m),
            (ServerRateKeys.LuckyGodsDaily, 25m));
        Check.Equal(12, ZeusGiftPolicy.GiftLimit(weekday), "cap.zeus_gifts_weekday raises weekday deliveries");
        Check.Equal(20, ZeusGiftPolicy.ExchangesForStones(0), "cap.zeus_exchanges_week replaces the base 5");
        Check.True(ZeusGiftPolicy.StonesAcceptedEveryDay, "event.zeus_stone_any_day lifts the weekday rule");
        ServerRates.ResetToDefaults();

        Check.True(
            QuestDailyState.AllowsCompletion(1, ServerRates.Current.Multiply(
                ServerRateKeys.QuestDailyCompletions, 1)) == false,
            "a once-a-day quest stays once a day at stock");
        Install((ServerRateKeys.QuestDailyCompletions, 3m));
        Check.True(
            QuestDailyState.AllowsCompletion(2, ServerRates.Current.Multiply(
                ServerRateKeys.QuestDailyCompletions, 1)),
            "cap.quest_daily 3 allows a third completion");
        Check.Equal(
            0,
            ServerRates.Current.Multiply(ServerRateKeys.QuestDailyCompletions, 0),
            "a one-time quest (cap 0) stays one-time");
        ServerRates.ResetToDefaults();
    }

    private static void CheckEventWindows()
    {
        // BATTLEFIELD2: these cases use the stock Pindus windows; the default is now the IGG times.
        Install((ServerRateKeys.BattlefieldPindusIggTimes, 0m));
        var calendar = RealmCalendar.CreateForTesting(RealmId.Tempest, "Asia/Manila");
        // Wednesday 2026-10-28 21:10 Manila = 13:10 UTC (Pindus opens 21:00).
        var wednesdayNine = new DateTimeOffset(2026, 10, 28, 13, 10, 0, TimeSpan.Zero);
        var wednesdaySeven = wednesdayNine.AddHours(-2);
        Check.True(
            BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.Pindus, calendar, wednesdayNine),
            "Pindus is open Wednesday 21:10 at stock");
        Check.True(
            !BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.Pindus, calendar, wednesdaySeven),
            "Pindus is closed Wednesday 19:10 at stock");
        Check.True(
            !BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.Pindus, calendar, wednesdayNine.AddMinutes(40)),
            "the stock window closes after 45 minutes");

        Install((ServerRateKeys.BattlefieldShiftHours, -2m), (ServerRateKeys.BattlefieldPindusIggTimes, 0m));
        Check.True(
            BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.Pindus, calendar, wednesdaySeven),
            "a -2 hour shift opens Pindus at 19:00");
        Check.True(
            !BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.Pindus, calendar, wednesdayNine),
            "a -2 hour shift closes the 21:00 window");

        Install((ServerRateKeys.BattlefieldWindowMinutes, 120m), (ServerRateKeys.BattlefieldPindusIggTimes, 0m));
        Check.True(
            BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.Pindus, calendar, wednesdayNine.AddMinutes(90)),
            "a 120-minute window is still open after 100 minutes");

        Install((ServerRateKeys.PindusAlwaysOpen, 1m));
        Check.True(
            BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.Pindus, calendar, wednesdaySeven.AddHours(-10)),
            "event.pindus_always_open opens Pindus at any time");
        Check.True(
            !BattlefieldSchedulePolicy.IsOpen(BattlefieldDestinationKind.NiMiniLower, calendar, wednesdaySeven),
            "Ni Mini Valley keeps its own windows");

        ServerRates.ResetToDefaults();
        // 23:30 Manila = 15:30 UTC.
        var lateEvening = new DateTimeOffset(2026, 10, 28, 15, 30, 0, TimeSpan.Zero);
        Check.True(
            LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.Wonderland, calendar, lateEvening) ==
                LegacyInstanceScheduleStatus.DailyCutoffPassed,
            "Wonderland is closed at 23:30 at stock");
        Install((ServerRateKeys.WonderlandCloseHour, 24m));
        Check.True(
            LegacyInstanceAdmissionPolicy.CheckSchedule(InstanceCallerEntryKind.Wonderland, calendar, lateEvening) ==
                LegacyInstanceScheduleStatus.Open,
            "event.wonderland_close_hour 24 keeps Wonderland open");
        ServerRates.ResetToDefaults();
    }

    private static void CheckOriginTemplatePlacement()
    {
        var published = new List<NpcSpawnDefinition>
        {
            new(0, "Sparta", "Sparta_084", "Sparta_084_Male1", 5081, 0f, 0f, 5081, 0x11, 0f, [], [])
        };
        GmNpcSpawnOverride Placement(string template, uint objectId) =>
            new(objectId, "check", 0, "GM_" + template, template, null, objectId, 0x11, 10f, 10f, 0f, true);
        var snapshot = GmNpcOverrideSnapshot.Create(
            [
                Placement("Sparta_035_FemMale9", 7101),
                Placement("Not_A_Client_Template", 7102)
            ],
            []);
        var placed = GmNpcDialoguePolicy.BuildPlacements(snapshot, 0, published);
        Check.True(
            placed.Any(static npc => npc.TemplateKey == "Sparta_035_FemMale9"),
            "Santa Claus (an Origin client template) can be placed in Sparta");
        Check.True(
            placed.All(static npc => npc.TemplateKey != "Not_A_Client_Template"),
            "a template the client does not ship is still refused");
    }

    // ROUND2: x1,000,000 saturates at the per-award caps instead of overflowing.
    private static void CheckHighRates()
    {
        Install((ServerRateKeys.ExperiencePill, 1_000_000m),
            (ServerRateKeys.MonsterExperience, 1_000_000m),
            (ServerRateKeys.DropChance, 1_000_000m));
        Check.Equal(1_000_000m, ServerRates.Current.Get(ServerRateKeys.MonsterExperience), "x1,000,000 is accepted");
        Check.True(
            PlayerExperienceItemPolicy.TryApply(10, 0, false, out var pill) &&
                pill.ExperienceGained == 100_000_000,
            "an EXP pill at x1,000,000 saturates at 100,000,000");
        Check.Equal(
            100_000_000,
            ServerRates.Current.Multiply(ServerRateKeys.MonsterExperience, int.MaxValue, 100_000_000),
            "a kill at x1,000,000 saturates at the settlement cap");
        Check.Equal(
            10_000,
            ServerRates.Current.MultiplyBasisPoints(ServerRateKeys.DropChance, 1),
            "drop chance x1,000,000 is clamped to 100%");
        Check.True(
            ServerRateCatalog.Definitions.All(static definition =>
                definition.Kind != ServerRateKind.Multiplier ||
                definition.MaximumValue <= 1_000_000m),
            "no multiplier goes above x1,000,000");
        ServerRates.ResetToDefaults();
    }

    private static void Install(params (string Key, decimal Value)[] values) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            values.Select(static value => KeyValuePair.Create(value.Key, value.Value)),
            1,
            DateTimeOffset.UtcNow,
            "check",
            "test"));
}
