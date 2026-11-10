using Godswar.Server.Application.Guilds;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Guilds;

namespace Godswar.Server.ProtocolChecks;

// GUILD (2026-11-03): guild building rules, altar curve, Lord hand-over
// switches, Member Advisor claims and guild quests (Development/build/guild).
internal static class GuildRulesChecks
{
    public const string CheckName = "Guild rules and services (guild)";

    public static Task RunAsync()
    {
        try
        {
            CheckRateDefaults();
            CheckBuildingPolicy();
            CheckBuildingLines();
            CheckAltarCurve();
            CheckAdvisor();
            CheckGuildQuests();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void Same(GuildBuildingOutcome expected, GuildBuildingOutcome actual, string description) =>
        Check.True(expected == actual, $"{description} (expected {expected}, got {actual})");

    private static void Install(string key, decimal value) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(key, value)], 1, DateTimeOffset.UtcNow, "check", "test"));

    private static void CheckRateDefaults()
    {
        ServerRates.ResetToDefaults();
        Check.Equal(9, ServerRateCatalog.Definitions.Count(static d => d.Category == GuildRateKeys.Category),
            "nine guild settings in the Guild category");
        // RELEASE: every guild switch defaults off (the admin turns them on).
        Check.True(!GuildRateKeys.On(GuildRateKeys.BuildingRules), "RELEASE: building rules default off");
        Check.True(!GuildRateKeys.On(GuildRateKeys.AltarCurve), "RELEASE: guide altar curve default off");
        Check.True(!GuildRateKeys.On(GuildRateKeys.LordRules), "RELEASE: Lord rules default off");
        Check.True(!GuildRateKeys.On(GuildRateKeys.DoubleClaims), "RELEASE: advisor claims default off");
        Check.True(!GuildRateKeys.On(GuildRateKeys.QuestRules),
            "guild quest rules default OFF (review fix: dailies stay open until guilds exist)");
        Check.Equal(5, GuildRateKeys.DutyOf(GuildRateKeys.BuildDuty), "Lord and Deputies build by default");
        Check.Equal(6, GuildRateKeys.DutyOf(GuildRateKeys.DeleteDuty), "only the Lord demolishes by default");
        Check.Equal(4, GuildRateKeys.DutyOf(GuildRateKeys.QuestReleaseDuty), "Directors and up release quests");
        Check.Equal(2L, ServerRates.Current.Absolute(GuildRateKeys.DoubleLevel), "double level defaults to 2");
        Install(GuildRateKeys.BuildingRules, 0m);
        Check.True(!GuildRateKeys.On(GuildRateKeys.BuildingRules), "building rules switch off");
        ServerRates.ResetToDefaults();
    }

    private static void CheckBuildingPolicy()
    {
        const int altar = 13; // Hephaestus
        (long, long)? altarL1 = (20_000, 200);
        Same(GuildBuildingOutcome.NoAuthority,
            GuildBuildingPolicy.DecideRaise(1, 5, altar, 1, 1, altarL1, 1_000_000, 1_000_000),
            "a plain member cannot build an altar");
        Same(GuildBuildingOutcome.NoAuthority,
            GuildBuildingPolicy.DecideRaise(4, 5, altar, 1, 1, altarL1, 1_000_000, 1_000_000),
            "a Director cannot build by default");
        Same(GuildBuildingOutcome.Done,
            GuildBuildingPolicy.DecideRaise(5, 5, altar, 1, 1, altarL1, 20_000, 200),
            "a Deputy builds when the purse holds exactly the price");
        Same(GuildBuildingOutcome.ShortOfGold,
            GuildBuildingPolicy.DecideRaise(6, 5, altar, 1, 1, altarL1, 20_000, 199),
            "one Gold short is refused");
        Same(GuildBuildingOutcome.ShortOfSilver,
            GuildBuildingPolicy.DecideRaise(6, 5, altar, 1, 1, altarL1, 19_999, 200),
            "one Silver short is refused");
        Same(GuildBuildingOutcome.FootstoneTooLow,
            GuildBuildingPolicy.DecideRaise(6, 5, altar, 2, 1, (30_000, 300), 1_000_000, 1_000_000),
            "an altar cannot go above the Footstone");
        Same(GuildBuildingOutcome.Done,
            GuildBuildingPolicy.DecideRaise(6, 5, GuildBuildingPolicy.FootstoneType, 2, 1, (10_000, 100), 10_000, 100),
            "the Footstone itself is not gated by its own level");
        Same(GuildBuildingOutcome.TopRank,
            GuildBuildingPolicy.DecideRaise(6, 5, 30, 9, 12, null, 1_000_000_000, 1_000_000_000),
            "God Altar level 9 does not exist (Upper Limit)");
        Same(GuildBuildingOutcome.FootstoneProtected,
            GuildBuildingPolicy.DecideDelete(6, 6, GuildBuildingPolicy.FootstoneType),
            "the Footstone cannot be demolished");
        Same(GuildBuildingOutcome.NoAuthority,
            GuildBuildingPolicy.DecideDelete(5, 6, altar), "a Deputy cannot demolish by default");
        Same(GuildBuildingOutcome.Done,
            GuildBuildingPolicy.DecideDelete(6, 6, altar), "the Lord demolishes");
        Check.Equal(1, GuildBuildingPolicy.EffectiveFootstoneLevel(null, 1), "no Footstone row = level 1");
        Check.Equal(4, GuildBuildingPolicy.EffectiveFootstoneLevel(4, 1), "the Footstone row wins over level 1");
        Check.Equal(3, GuildBuildingPolicy.EffectiveFootstoneLevel(null, 3), "a raised guild level counts");
        Check.Equal(1, GuildBuildingPolicy.TargetLevel(true, 0), "building targets level 1");
        Check.Equal(5, GuildBuildingPolicy.TargetLevel(false, 4), "an upgrade targets the next level");
    }

    private static void CheckBuildingLines()
    {
        Check.Equal(1005, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.Done, GuildBuildingAction.Build), "built line");
        Check.Equal(1006, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.Done, GuildBuildingAction.Upgrade), "upgraded line");
        Check.Equal(1008, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.Done, GuildBuildingAction.Delete), "demolished line");
        Check.Equal(1002, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.ShortOfGold, GuildBuildingAction.Build), "short of Gold line");
        Check.Equal(1003, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.ShortOfSilver, GuildBuildingAction.Upgrade), "short of Silver line");
        Check.Equal(1004, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.TopRank, GuildBuildingAction.Upgrade), "top rank line");
        Check.Equal(1007, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.NoSuchBuilding, GuildBuildingAction.Delete), "no such building line");
        Check.Equal(1000, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.NotAMember, GuildBuildingAction.Build), "not a member line");
        Check.Equal(1016, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.NoAuthority, GuildBuildingAction.Delete), "no authority line");
        Check.Equal(1016, GameClientHandler.GuildBuildingLine(GuildBuildingOutcome.FootstoneTooLow, GuildBuildingAction.Upgrade), "Footstone line");
    }

    private static void CheckAltarCurve()
    {
        Check.Equal(0d, GuildAltarWorshipBonusPolicy.BonusPercent(0, true), "no points, no bonus");
        Check.Equal(0.001d, GuildAltarWorshipBonusPolicy.BonusPercent(1, true), "one point already counts");
        Check.Equal(1d, GuildAltarWorshipBonusPolicy.BonusPercent(1_000, true), "1,000 points = +1%");
        Check.Equal(50d, GuildAltarWorshipBonusPolicy.BonusPercent(50_000, true), "50,000 = 50% (NF_L0_GH87)");
        Check.Equal(150d, GuildAltarWorshipBonusPolicy.BonusPercent(150_000, true), "the 1.5x ceiling at 150,000");
        Check.Equal(150d, GuildAltarWorshipBonusPolicy.BonusPercent(1_000_000, true), "the ceiling holds");
        // Regression: the old curve, exactly as before, when the switch is off.
        Check.Equal(0d, GuildAltarWorshipBonusPolicy.BonusPercent(49_999, false), "old curve: nothing below 50,000");
        Check.Equal(7.5d, GuildAltarWorshipBonusPolicy.BonusPercent(50_000, false), "old curve: 7.5% per step");
        Check.Equal(150d, GuildAltarWorshipBonusPolicy.BonusPercent(1_000_000, false), "old curve: ceiling at 1,000,000");
        var noLoss = true;
        for (long points = 0; points <= 1_200_000; points += 777)
        {
            noLoss &= GuildAltarWorshipBonusPolicy.BonusPercent(points, true) >=
                GuildAltarWorshipBonusPolicy.BonusPercent(points, false);
        }

        Check.True(noLoss, "no balance pays less under the guide curve");
        ServerRates.ResetToDefaults();
        Install(GuildRateKeys.AltarCurve, 1m); // RELEASE: the guide curve defaults off now
        Check.Equal(5, GuildAltarWorshipBonusPolicy.BonusFor(1_000, 540), "Hephaestus L12 base 540 x 1% = 5");
        Check.Equal(810, GuildAltarWorshipBonusPolicy.BonusFor(150_000, 540), "Hephaestus L12 at the ceiling = 810");
        Install(GuildRateKeys.AltarCurve, 0m);
        Check.Equal(0, GuildAltarWorshipBonusPolicy.BonusFor(1_000, 540), "switch off: 1,000 points pays nothing again");
        Check.Equal(41, GuildAltarWorshipBonusPolicy.BonusFor(50_000, 540), "switch off: 50,000 pays 7.5%");
        ServerRates.ResetToDefaults();
    }

    private static void CheckAdvisor()
    {
        Check.Equal((1, 1000, false), GameClientHandler.GuildDoubleClaimOf(11)!.Value, "UNION11 = 1 h EXP, status 1000");
        Check.Equal((4, 1003, false), GameClientHandler.GuildDoubleClaimOf(14)!.Value, "UNION14 = 4 h EXP, status 1003");
        Check.Equal((24, 1009, false), GameClientHandler.GuildDoubleClaimOf(20)!.Value, "UNION20 = 24 h EXP, status 1009");
        Check.Equal((1, 1010, true), GameClientHandler.GuildDoubleClaimOf(21)!.Value, "UNION21 = 1 h TP, status 1010");
        Check.Equal((24, 1019, true), GameClientHandler.GuildDoubleClaimOf(30)!.Value, "UNION30 = 24 h TP, status 1019");
        Check.True(GameClientHandler.GuildDoubleClaimOf(10) is null && GameClientHandler.GuildDoubleClaimOf(31) is null,
            "other entries are not claims");
        Check.Equal(2, GameClientHandler.GuildDoubleWeeklyHours(0), "level 0 = 2 hours (level + 2)");
        Check.Equal(4, GameClientHandler.GuildDoubleWeeklyHours(2), "default level 2 = 4 hours");
        Check.Equal(24, GameClientHandler.GuildDoubleWeeklyHours(99), "level caps at 22 = 24 hours");
        Check.True(GameClientHandler.GuildDoubleInquiryLines(2, 4, 1).SequenceEqual([1531, 2003, 504]),
            "inquiry encodes level 2, 4 EXP hours and 1 TP hour the way the script decodes them");
    }

    private static void CheckGuildQuests()
    {
        Check.Equal(32, GuildQuestCatalog.QuestIds.Count, "32 guild quests carry a guild reward");
        Check.True(GuildQuestCatalog.TryFind(210, out var leader) &&
            leader == new GuildQuestReward(GuildQuestCatalog.KillKind, 30, 1600, 3000),
            "quest 210 pays Guild Gold 30, Guild Fund 1600, Contribution 3000");
        Check.True(GuildQuestCatalog.TryFind(207, out var letter) &&
            letter == new GuildQuestReward(GuildQuestCatalog.DeliveryKind, 0, 200, 200),
            "quest 207 is a delivery paying Guild Fund 200, Contribution 200");
        Check.True(!GuildQuestCatalog.TryFind(389, out _), "the War Material chain is not a guild-reward quest");
        var consistent = GuildQuestCatalog.QuestIds.All(static id =>
            StarterQuestChain.Find(id) is { } step &&
            step.UIQuestSort == StarterQuestChain.SortGuild &&
            GuildQuestCatalog.TryFind(id, out var reward) &&
            step.Gold == reward.GuildGold);
        Check.True(consistent, "every catalog quest is a sort-4 chain quest whose Gold column is the Guild Gold");

        var payout = new QuestRewardPayout(2000, 2, 0, 30);
        Check.Equal(payout, GameClientHandler.GuildQuestPersonalPayout(210, payout, inGuild: true),
            "default (rules off since the review fix): the stock payout is untouched");
        Install(GuildRateKeys.QuestRules, 1m);
        Check.Equal(new QuestRewardPayout(2000, 2, 0, 0), GameClientHandler.GuildQuestPersonalPayout(210, payout, inGuild: true),
            "rules on: the Guild Gold is not paid to a guild member");
        Check.Equal(payout, GameClientHandler.GuildQuestPersonalPayout(210, payout, inGuild: false),
            "rules on, no guild at hand-in: the player keeps the gold (nothing is lost)");
        var other = new QuestRewardPayout(500, 1, 10, 7);
        Check.Equal(other, GameClientHandler.GuildQuestPersonalPayout(520, other, inGuild: true),
            "regression: a non-guild quest's payout is untouched");
        Install(GuildRateKeys.QuestRules, 0m);
        Check.Equal(payout, GameClientHandler.GuildQuestPersonalPayout(210, payout, inGuild: true),
            "rules off: the stock payout is untouched");
        ServerRates.ResetToDefaults();
    }
}
