using Godswar.Server.Application.Seasons;
using Rules = Godswar.Server.Application.Seasons.SpringFestivalRules;

namespace Godswar.Server.ProtocolChecks;

/// <summary>ROUND3 W3 Spring Festival: drops, envoy pages and rewards, the Lunar Beast Slayer's lines.</summary>
internal static class SpringFestivalChecks
{
    public const string CheckName = "W3 Spring Festival: drops, envoy pages, slayer";

    public static Task RunAsync()
    {
        var t0 = new DateTimeOffset(2027, 1, 27, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2027, 2, 6, 0, 0, 0, TimeSpan.Zero);
        try
        {
            SeasonalEvents.Install(SeasonalEvents.Parse($"spring_festival:1:{t0.ToUnixTimeSeconds()}:{t1.ToUnixTimeSeconds()}:1"));
            var at = t0.AddDays(2);
            // Death ids are hashes of the death identity (MonsterDeathRewardCommandEnvelope), so hashed test ids.
            var ids = Enumerable.Range(0, 4_000).Select(static i =>
                new Guid(System.Security.Cryptography.MD5.HashData(BitConverter.GetBytes(i)))).ToList();

            var all = ids.SelectMany(g => Rules.Drops(2, g, at, 100, 0)).ToList();
            Check.True(all.Count == ids.Count && all.All(static d => d.Index == 0 && d.ItemId is 3963 or 3964 or 3965 or 3966
                or 3967 or 4006 or 4034 or 4007 or 4035), "100 %: every Athens-suburb kill drops one festival good");
            Check.True(all.Select(static d => d.ItemId).Distinct().Count() == 9, "all nine goods appear");
            var fire = ids.SelectMany(g => Rules.Drops(4, g, at, 0, 100)).ToList();
            Check.True(fire.Count == ids.Count && fire.All(static d => d.ItemId == 14041), "firecracker at 100 % in the Sparta suburbs");
            Check.True(!ids.SelectMany(g => Rules.Drops(2, g, at, 0, 0)).Any(), "0 % drops nothing");
            Check.True(!Rules.Drops(11, ids[1], at, 100, 100).Any(), "no drops outside the suburbs");
            Check.True(!Rules.Drops(2, ids[1], t1.AddDays(1), 100, 100).Any(), "no drops off-season");
            Check.True(Rules.Drops(2, ids[1], t0.AddYears(1).AddDays(1), 100, 100).Count() == 2, "yearly window opens again");
            var tenPercent = ids.SelectMany(g => Rules.Drops(4, g, at, 10, 0)).Count();
            Check.True(tenPercent is > 250 and < 550, $"10 % goods rate is roughly 10 % ({tenPercent}/4000)");

            Check.True(Rules.OpeningMenu.SequenceEqual([100, 101, 102, 103, 104, 105]), "page 1 = the six topics");
            Check.True(Rules.TopicPage(100)!.SequenceEqual([200, 201, 202]) && Rules.TopicPage(105)!.SequenceEqual([211, 212]) &&
                       Rules.TopicPage(106) is null, "page 2 buttons per topic");
            Check.True(Rules.Exchanges.Keys.Order().SequenceEqual(Enumerable.Range(200, 13)), "every page-2 button has an exchange");
            Check.True(Rules.PlateReward(200, 1) == (500, 2) && Rules.PlateReward(200, 120) == (15_000, 5) &&
                       Rules.PlateReward(201, 61) == (30_000, 15) && Rules.PlateReward(202, 150) == (300_000, 25),
                "2010 level bands (Lv121+ keep the last band)");
            Check.True(Rules.RequiredGender(211) == 1 && Rules.RequiredGender(212) == 0 && Rules.RequiredGender(203) == -1,
                "couple exchange: 211 male, 212 female");
            Check.True(Rules.ItemReward(207) == 3939 && Rules.ItemReward(210) == 3949 && Rules.ItemReward(206) == 4231,
                "fireworks and the crystal from the button texts");

            Check.True(Rules.Bonus(0) == (101, 4230, 10) && Rules.Bonus(1_755) == (105, 14340, 1) &&
                       Rules.Bonus(1_799) == (106, 11065, 1) && Rules.Bonus(9_999).Result == 107, "slayer lucky finds");
            Check.True(Rules.CountLine(0) == 9 && Rules.CountLine(37) == 37_009 && (Rules.CountLine(250) - 1) / 1000 == 250 &&
                       Rules.CountLine(250) % 1000 == 9, "count line n*1000+9, as NpcFunFire.lua decodes it");
            Check.True(Rules.DrivesOffLunarBeast(250) && Rules.DrivesOffLunarBeast(500) && !Rules.DrivesOffLunarBeast(0) &&
                       !Rules.DrivesOffLunarBeast(251), "every 250th firecracker drives off the Lunar Beast");
            Check.True(Rules.RateDefinitions.Count == 3 && Rules.RateDefinitions.All(static r => r.Key.StartsWith("season.spring_")),
                "three editor rates");
        }
        finally
        {
            SeasonalEvents.Install([]);
        }

        return Task.CompletedTask;
    }
}
