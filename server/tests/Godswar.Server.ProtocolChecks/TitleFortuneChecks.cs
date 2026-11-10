using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Titles;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.ProtocolChecks;

// TITLES: the Title Expert and Fortune Teller rules, their switches and the Lucky Day bonus.
internal static class TitleFortuneChecks
{
    public const string CheckName = "Title Expert and Fortune Teller follow the IGG tables, client numbers and are off by default";

    public static Task RunAsync()
    {
        ServerRates.ResetToDefaults();
        var defaults = ServerRatesSnapshot.Default;
        Check.Equal(0m, defaults.Get(ServerRateKeys.TitleExpertEnabled), "title expert off by default");
        Check.Equal(0m, defaults.Get(ServerRateKeys.FortuneTellerEnabled), "fortune teller off by default");
        Check.Equal(230m, defaults.Get(ServerRateKeys.FortuneTellerGold), "client button price 230 Gold");
        Check.Equal(5m, defaults.Get(ServerRateKeys.ZodiacLuckyDayBonus), "retail Lucky Day +5%");
        if (Environment.GetEnvironmentVariable("GODSWAR_TITLE_EXPERT") is null)
        {
            Check.True(!TitleExpertPolicy.IsEnabled(), "Title Expert disabled with default rates");
        }

        if (Environment.GetEnvironmentVariable("GODSWAR_FORTUNE_TELLER") is null)
        {
            Check.True(!FortuneTellerPolicy.IsEnabled(), "Fortune Teller disabled with default rates");
        }

        // Leveling titles: IGG table. Warrior 40/250 .. 100/2400, judged by physical attack.
        var none = Array.Empty<uint>();
        TitleExpertPolicy.TitleStats Stats(int level, int patk = 0, int matk = 0, int hp = 0, int pdef = 0,
            int mdef = 0, int hit = 0, int dodge = 0, int absorb = 0) =>
            new(level, hp, patk, matk, pdef, mdef, hit, dodge, absorb);

        var claim = TitleExpertPolicy.ResolveLevelClaim(0, Stats(39, patk: 5000), none);
        Check.True(claim.Grant.Count == 0 && claim.Reply.SequenceEqual([501]), "warrior level 39: requirement line 501");
        claim = TitleExpertPolicy.ResolveLevelClaim(0, Stats(40, patk: 250), none);
        Check.True(claim.Grant.SequenceEqual([1000u]) && claim.Reply.SequenceEqual([1000]), "warrior 40/250 -> 1000");
        claim = TitleExpertPolicy.ResolveLevelClaim(0, Stats(60, patk: 599), [1000u]);
        Check.True(claim.Grant.Count == 0 && claim.Reply.SequenceEqual([502]), "warrior 60/599 owning 1000 -> line 502");
        claim = TitleExpertPolicy.ResolveLevelClaim(0, Stats(120, patk: 3000), none);
        Check.True(claim.Grant.SequenceEqual([1000u, 1001u, 1002u, 1003u]), "warrior 120/3000 gets all four in order");
        claim = TitleExpertPolicy.ResolveLevelClaim(0, Stats(120, patk: 3000), [1000u, 1001u, 1002u, 1003u]);
        Check.True(claim.Reply.SequenceEqual([600]), "all owned -> 600");
        claim = TitleExpertPolicy.ResolveLevelClaim(1, Stats(80, patk: 1800), none);
        Check.True(claim.Grant.SequenceEqual([1100u, 1101u, 1102u]), "champion 80/1800 -> 1100-1102");
        claim = TitleExpertPolicy.ResolveLevelClaim(2, Stats(60, patk: 9999, matk: 399), none);
        Check.True(claim.Grant.SequenceEqual([1200u]), "priest is judged by magic attack (400 needed for 1201)");
        claim = TitleExpertPolicy.ResolveLevelClaim(3, Stats(40, matk: 219), none);
        Check.True(claim.Reply.SequenceEqual([530]), "mage 40/219 -> line 530");
        claim = TitleExpertPolicy.ResolveLevelClaim(3, Stats(100, matk: 2100), none);
        Check.True(claim.Grant.SequenceEqual([1300u, 1301u, 1302u, 1303u]), "mage 100/2100 -> 1300-1303");

        // Attribute titles: HP 20000/32000 ... Absorb 900/1300 (buttons 1-8).
        claim = TitleExpertPolicy.ResolveAttributeClaim(1, Stats(1, hp: 19999), none);
        Check.True(claim.Reply.SequenceEqual([501]), "HP 19999 -> 501");
        claim = TitleExpertPolicy.ResolveAttributeClaim(1, Stats(1, hp: 32000), none);
        Check.True(claim.Grant.SequenceEqual([3000u, 3001u]), "HP 32000 -> 3000 and 3001");
        claim = TitleExpertPolicy.ResolveAttributeClaim(4, Stats(1, pdef: 2100), none);
        Check.True(claim.Grant.SequenceEqual([3006u]), "DEF 2100 -> Iron Bastion only");
        claim = TitleExpertPolicy.ResolveAttributeClaim(4, Stats(1, pdef: 2100), [3006u]);
        Check.True(claim.Reply.SequenceEqual([516]), "DEF 2100 owning 3006 -> 516");
        claim = TitleExpertPolicy.ResolveAttributeClaim(8, Stats(1, absorb: 1300), [3014u, 3015u]);
        Check.True(claim.Reply.SequenceEqual([600]), "both absorb titles owned -> 600");
        claim = TitleExpertPolicy.ResolveAttributeClaim(9, Stats(1), none);
        Check.True(claim.Reply.SequenceEqual([500]), "unknown attribute -> 500");
        int[] firstLines = [501, 505, 510, 515, 520, 525, 530, 535];
        for (var button = 1; button <= 8; button++)
        {
            claim = TitleExpertPolicy.ResolveAttributeClaim(button, Stats(1), none);
            Check.True(claim.Reply.SequenceEqual([firstLines[button - 1]]), $"attribute {button} first line");
            var top = Stats(1, 99999, 99999, 99999, 99999, 99999, 99999, 99999, 99999);
            claim = TitleExpertPolicy.ResolveAttributeClaim(button, top, none);
            Check.True(
                claim.Grant.SequenceEqual([(uint)(3000 + 2 * (button - 1)), (uint)(3001 + 2 * (button - 1))]),
                $"attribute {button} title ids");
        }

        // Quest titles: the mapped quests that exist in the server's chain.
        foreach (var questId in new uint[] { 118, 122, 217, 221, 376, 1118, 1122, 1217, 1221, 1376 })
        {
            Check.True(StarterQuestChain.Find(questId) is not null, $"quest {questId} is in the chain");
        }

        var missing = TitleExpertPolicy.MissingQuestTitles([1118u, 1376u, 5u, 1118u], [2002u]);
        Check.True(missing.SequenceEqual([2506u]), "missing quest titles skip owned and unmapped quests");

        // Fortune Teller pages.
        Check.True(FortuneTellerPolicy.PageTwo(1).SequenceEqual(Enumerable.Range(1, 12)), "free change opens 1-12");
        Check.True(FortuneTellerPolicy.PageTwo(2).SequenceEqual(Enumerable.Range(13, 12)), "gold change opens 13-24");
        Check.True(FortuneTellerPolicy.PageTwo(3).SequenceEqual([26]), "free lucky opens 26");
        Check.True(FortuneTellerPolicy.PageTwo(4).SequenceEqual([39]), "gold lucky opens 39");
        Check.True(FortuneTellerPolicy.ResolvePageTwo(1, 1) == (FortuneTellerAction.FreeSignChange, (byte)0), "1 then 1 = free Aries");
        Check.True(FortuneTellerPolicy.ResolvePageTwo(2, 24) == (FortuneTellerAction.GoldSignChange, (byte)11), "2 then 24 = gold Pisces");
        Check.True(FortuneTellerPolicy.ResolvePageTwo(1, 13) is null, "13 is not on the free page");
        Check.True(FortuneTellerPolicy.ResolvePageTwo(3, 39) is null, "39 is not on the free lucky page");

        var now = new DateTimeOffset(2026, 11, 4, 3, 0, 0, TimeSpan.Zero); // Wed 11:00 realm time
        var state = new FortuneTellerPolicy.FortuneState(2, 0, null, 1000, false);
        Check.Equal(1, FortuneTellerPolicy.Decide(FortuneTellerAction.FreeSignChange, 5, state, 230, now).Line, "free change ok");
        Check.Equal(100, FortuneTellerPolicy.Decide(FortuneTellerAction.FreeSignChange, 2, state, 230, now).Line, "same sign");
        Check.Equal(101, FortuneTellerPolicy.Decide(FortuneTellerAction.FreeSignChange, 5, state with { FreeUsedInWindow = true }, 230, now).Line, "monthly limit");
        var paid = FortuneTellerPolicy.Decide(FortuneTellerAction.GoldSignChange, 5, state with { FreeUsedInWindow = true }, 230, now);
        Check.True(paid.Line == 1 && paid.GoldCost == 230 && paid.ChangesSign, "gold change takes 230");
        Check.Equal(102, FortuneTellerPolicy.Decide(FortuneTellerAction.GoldSignChange, 5, state with { Gold = 229 }, 230, now).Line, "not enough gold");
        var lucky = FortuneTellerPolicy.Decide(FortuneTellerAction.FreeLuckyDay, 0, state, 230, now);
        Check.True(lucky.Line == 103 && lucky.StartsLucky && lucky.GoldCost == 0, "free lucky day");
        Check.Equal(106, FortuneTellerPolicy.Decide(FortuneTellerAction.FreeLuckyDay, 0, state with { FreeUsedInWindow = true }, 230, now).Line, "weekly limit");
        var active = state with { LuckyStatus = 1, LuckyExpiresAt = now.AddHours(1) };
        Check.Equal(104, FortuneTellerPolicy.Decide(FortuneTellerAction.GoldLuckyDay, 0, active, 230, now).Line, "lucky time not used up");
        var expired = state with { LuckyStatus = 1, LuckyExpiresAt = now.AddSeconds(-1), FreeUsedInWindow = true };
        var paidLucky = FortuneTellerPolicy.Decide(FortuneTellerAction.GoldLuckyDay, 0, expired, 230, now);
        Check.True(paidLucky.Line == 103 && paidLucky.GoldCost == 230, "gold lucky day after the last expired");

        Check.Equal(new DateTimeOffset(2026, 11, 1, 16, 0, 0, TimeSpan.Zero), FortuneTellerPolicy.WeekStart(now, TimeSpan.FromHours(8)), "week starts Monday 00:00 UTC+8");
        Check.Equal(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero), FortuneTellerPolicy.MonthStart(now, TimeSpan.FromHours(8)), "month starts on the 1st 00:00 UTC+8");
        var sundayNight = new DateTimeOffset(2026, 11, 1, 15, 59, 0, TimeSpan.Zero); // Sun 23:59 realm time
        Check.Equal(new DateTimeOffset(2026, 10, 25, 16, 0, 0, TimeSpan.Zero), FortuneTellerPolicy.WeekStart(sundayNight, TimeSpan.FromHours(8)), "Sunday 23:59 is still last week");
        Check.Equal(new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero), FortuneTellerPolicy.WeekStart(now, TimeSpan.FromHours(10)), "Brisbane week start");

        Check.Equal(1050, FortuneTellerPolicy.ApplyLuckyBonus(1000, 5m, int.MaxValue), "+5%");
        Check.Equal(19, FortuneTellerPolicy.ApplyLuckyBonus(19, 5m, int.MaxValue) - 0, "19 * 1.05 truncates to 19");
        Check.Equal(0, FortuneTellerPolicy.ApplyLuckyBonus(0, 5m, int.MaxValue), "zero stays zero");
        Check.Equal(100, FortuneTellerPolicy.ApplyLuckyBonus(99, 5m, 100), "saturates at the cap");
        Check.Equal(1000, FortuneTellerPolicy.ApplyLuckyBonus(1000, 0m, int.MaxValue), "0% passes through");
        Check.True(FortuneTellerPolicy.IsLuckyActive(1, now.AddHours(24), now), "active within 24 h");
        Check.True(!FortuneTellerPolicy.IsLuckyActive(1, null, now), "no expiry = not ours, not active");
        Check.True(!FortuneTellerPolicy.IsLuckyActive(0, now.AddHours(1), now), "status 0 is not active");
        return Task.CompletedTask;
    }
}
