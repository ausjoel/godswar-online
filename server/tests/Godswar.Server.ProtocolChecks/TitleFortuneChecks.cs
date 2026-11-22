using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Titles;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// TITLES: the Title Expert and Fortune Teller rules, their switches and the Lucky Day bonus.
internal static class TitleFortuneChecks
{
    public const string CheckName = "Title Expert and Fortune Teller follow the IGG tables, client numbers and are off by default";

    public static Task RunAsync()
    {
        ServerRates.ResetToDefaults();
        var defaults = ServerRatesSnapshot.Default;
        Check.Equal(1m, defaults.Get(ServerRateKeys.TitleExpertEnabled), "B21: title expert on by default");
        Check.Equal(1m, defaults.Get(ServerRateKeys.FortuneTellerEnabled), "B21: fortune teller on by default");
        // B21: the "disabled" checks below run with both switches set to 0.
        ServerRates.Install(ServerRatesSnapshot.Create([KeyValuePair.Create(ServerRateKeys.TitleExpertEnabled, 0m),
            KeyValuePair.Create(ServerRateKeys.FortuneTellerEnabled, 0m)], 1, DateTimeOffset.UtcNow, "check", "test"));
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

        // Attribute titles: HP 20000/32000 ... Absorb 900/1300 (buttons 1-8), tiers 1-2 shared by every class.
        // B22: professions 0 Warrior, 1 Champion, 2 Priest, 3 Mage; tiers 3-8 per class (ids 3016-3063).
        claim = TitleExpertPolicy.ResolveAttributeClaim(0, 1, Stats(1, hp: 19999), none);
        Check.True(claim.Reply.SequenceEqual([501]), "HP 19999 -> 501");
        claim = TitleExpertPolicy.ResolveAttributeClaim(0, 1, Stats(1, hp: 32000), none);
        Check.True(claim.Grant.SequenceEqual([3000u, 3001u]), "HP 32000 -> 3000 and 3001");
        claim = TitleExpertPolicy.ResolveAttributeClaim(0, 4, Stats(1, pdef: 2100), none);
        Check.True(claim.Grant.SequenceEqual([3006u]), "DEF 2100 -> Iron Bastion only");
        claim = TitleExpertPolicy.ResolveAttributeClaim(0, 4, Stats(1, pdef: 2100), [3006u]);
        Check.True(claim.Reply.SequenceEqual([516]), "DEF 2100 owning 3006 -> 516");
        claim = TitleExpertPolicy.ResolveAttributeClaim(0, 9, Stats(1), none);
        Check.True(claim.Reply.SequenceEqual([500]), "unknown attribute -> 500");
        int[] firstLines = [501, 505, 510, 515, 520, 525, 530, 535];
        var top = Stats(1, 999999, 999999, 999999, 999999, 999999, 999999, 999999, 999999);
        for (byte profession = 0; profession < 4; profession++)
        {
            for (var button = 1; button <= 8; button++)
            {
                claim = TitleExpertPolicy.ResolveAttributeClaim(profession, button, Stats(1), none);
                Check.True(claim.Reply.SequenceEqual([firstLines[button - 1]]), $"class {profession} attribute {button} first line");
                claim = TitleExpertPolicy.ResolveAttributeClaim(profession, button, top, none);
                var expected = new List<uint> { (uint)(3000 + 2 * (button - 1)), (uint)(3001 + 2 * (button - 1)) };
                if (TitleExpertPolicy.HasLadder(profession, button))
                {
                    expected.AddRange(Enumerable.Range(0, 6).Select(i => (uint)(3016 + 6 * (button - 1) + i)));
                }

                Check.True(claim.Grant.SequenceEqual(expected), $"B22: class {profession} attribute {button} top grants");
                claim = TitleExpertPolicy.ResolveAttributeClaim(profession, button, top, expected);
                Check.True(claim.Reply.SequenceEqual([TitleExpertPolicy.HasLadder(profession, button)
                        ? 600 : 7000 + profession * 100 + button * 10]),
                    $"B22: class {profession} attribute {button} all owned -> 600 or not-for-class line");
                var tiers = TitleExpertPolicy.AttributeTiersFor(profession, button)!;
                for (var i = 1; i < tiers.Count; i++)
                {
                    Check.True(tiers[i].Need > tiers[i - 1].Need, $"B22: class {profession} attribute {button} tier {i + 1} rises");
                }
            }
        }

        // B22: the ladder per class. ATK is Warrior/Champion only, MATK Priest/Mage only; the rest all four.
        Check.True(TitleExpertPolicy.HasLadder(0, 2) && TitleExpertPolicy.HasLadder(1, 2) &&
            !TitleExpertPolicy.HasLadder(2, 2) && !TitleExpertPolicy.HasLadder(3, 2), "B22: ATK ladder for physical classes");
        Check.True(!TitleExpertPolicy.HasLadder(0, 3) && !TitleExpertPolicy.HasLadder(1, 3) &&
            TitleExpertPolicy.HasLadder(2, 3) && TitleExpertPolicy.HasLadder(3, 3), "B22: MATK ladder for magic classes");
        claim = TitleExpertPolicy.ResolveAttributeClaim(0, 1, Stats(1, hp: 86699), [3000u, 3001u]);
        Check.True(claim.Grant.Count == 0 && claim.Reply.SequenceEqual([7013]), "B22: Warrior HP 86699 -> line 7013 (needs 86700)");
        claim = TitleExpertPolicy.ResolveAttributeClaim(0, 1, Stats(1, hp: 86700), [3000u, 3001u]);
        Check.True(claim.Grant.SequenceEqual([3016u]), "B22: Warrior HP 86700 -> Titan Blood");
        claim = TitleExpertPolicy.ResolveAttributeClaim(3, 1, Stats(1, hp: 86700), [3000u, 3001u]);
        Check.True(claim.Grant.SequenceEqual([3016u, 3017u]), "B22: Mage HP 86700 -> Titan Blood and Heart of Atlas (59.8k/74.8k)");
        claim = TitleExpertPolicy.ResolveAttributeClaim(3, 1, Stats(1, hp: 134599), [3000u, 3001u]);
        Check.True(claim.Grant.SequenceEqual([3016u, 3017u, 3018u, 3019u, 3020u]), "B22: Mage HP 134599 stops before tier 8");
        claim = TitleExpertPolicy.ResolveAttributeClaim(3, 1, Stats(1, hp: 134599), [3000u, 3001u, 3016u, 3017u, 3018u, 3019u, 3020u]);
        Check.True(claim.Reply.SequenceEqual([7318]), "B22: Mage HP tier 8 line 7318");
        claim = TitleExpertPolicy.ResolveAttributeClaim(1, 2, Stats(1, patk: 18000), [3002u, 3003u]);
        Check.True(claim.Grant.SequenceEqual([3022u, 3023u, 3024u, 3025u, 3026u, 3027u]), "B22: Champion ATK 18000 -> top");
        claim = TitleExpertPolicy.ResolveAttributeClaim(2, 2, Stats(1, patk: 99999), [3002u, 3003u]);
        Check.True(claim.Reply.SequenceEqual([7220]), "B22: Priest ATK after tier 2 -> not-for-class line 7220");
        claim = TitleExpertPolicy.ResolveAttributeClaim(3, 4, Stats(1, pdef: 5420), [3006u, 3007u]);
        Check.True(claim.Grant.Count == 6 && claim.Grant[^1] == 3039u, "B22: Mage DEF 5420 (90% of 6019) -> Unbreakable Olympus");
        claim = TitleExpertPolicy.ResolveAttributeClaim(0, 8, Stats(1, absorb: 39900), [3014u]);
        Check.True(claim.Grant.SequenceEqual([3015u, 3058u, 3059u, 3060u, 3061u, 3062u, 3063u]), "B22: Warrior absorb 39900 skips owned tier 1");
        Check.Equal(48, TitleExpertPolicy.LadderTitleText.Count, "B22: 48 ladder names");
        Check.True(Enumerable.Range(3016, 48).All(id => TitleExpertPolicy.LadderTitleText.ContainsKey((uint)id)),
            "B22: names cover 3016-3063");
        Check.Equal(3063u, TitleExpertPolicy.LadderTitleId(8, 8), "B22: last ladder id");

        // B22: a claim never takes the character past the 128 owned-title limit.
        var big = new TitleExpertPolicy.Claim([3016u, 3017u, 3018u], [3016, 3017, 3018]);
        Check.True(TitleExpertPolicy.FitToTitleLimit(big, 125, 128).Grant.Count == 3, "B22: 125 + 3 fits 128");
        var trimmed = TitleExpertPolicy.FitToTitleLimit(big, 126, 128);
        Check.True(trimmed.Grant.SequenceEqual([3016u, 3017u]) && trimmed.Reply.SequenceEqual([3016, 3017]), "B22: 126 + 3 trims to 2");
        Check.True(TitleExpertPolicy.FitToTitleLimit(big, 128, 128).Reply.SequenceEqual([500]), "B22: full list -> 500");
        var line = new TitleExpertPolicy.Claim([], [7013]);
        Check.True(TitleExpertPolicy.FitToTitleLimit(line, 128, 128).Reply.SequenceEqual([7013]), "B22: requirement line passes at the limit");

        // B22: the Title window tab follows the id range (DesigType0-4); Medusa/event titles stay Event (4).
        Check.Equal((byte)0, PacketBuilder.DesignationTypeFor(1000), "B22: 1000 Leveling tab");
        Check.Equal((byte)1, PacketBuilder.DesignationTypeFor(2506), "B22: 2506 Quest tab");
        Check.Equal((byte)2, PacketBuilder.DesignationTypeFor(3016), "B22: 3016 Attribute tab");
        Check.Equal((byte)4, PacketBuilder.DesignationTypeFor(1401), "B22: engagement stays Event");
        Check.Equal((byte)4, PacketBuilder.DesignationTypeFor(5152), "B22: Medusa stays Event");

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
