using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Villagers;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// VILLAGERS: Village Leader book re-issue and Festival Envoy weekend claim / gifts.
internal static class VillagersChecks
{
    public const string CheckName = "Village Leader books and Festival Envoy (villagers)";

    public static Task RunAsync()
    {
        try
        {
            CheckSwitchDefaults();
            CheckWeekendLever();
            CheckBooks();
            CheckFestivalPages();
            CheckReplyFrame();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void Install(string key, decimal value) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(key, value)],
            1, DateTimeOffset.UtcNow, "check", "test"));

    private static void CheckSwitchDefaults()
    {
        ServerRates.ResetToDefaults();
        Check.True(!VillagersSwitches.WeekendClaimEnabled, "RELEASE: weekend claim is off by default");
        Check.True(!VillagersSwitches.WeekendClaimAnyDay, "weekend claim any-day lever is off by default");
        Check.True(!VillagersSwitches.FestivalGiftsEnabled, "holiday gifts are off by default");
        Check.True(!VillagersSwitches.VillageLeaderBooksEnabled, "RELEASE: Village Leader re-issue is off by default");
        Check.Equal(4, ServerRateCatalog.Definitions.Count(static definition =>
            definition.Category == ServerRateCatalog.VillagersCategory), "four villagers switches");
        Check.True(ServerRateCatalog.Definitions.All(static definition =>
            definition.Category != ServerRateCatalog.VillagersCategory ||
            definition is { Kind: ServerRateKind.Absolute, MinimumValue: 0m, MaximumValue: 1m }),
            "villagers switches are 0/1");
        Check.Equal(
            ServerRateCatalog.Definitions.Count,
            ServerRateCatalog.Definitions.Select(static definition => definition.Key)
                .Distinct(StringComparer.Ordinal).Count(),
            "rate keys stay unique");
        Install(ServerRateKeys.FestivalGifts, 1m);
        Check.True(VillagersSwitches.FestivalGiftsEnabled, "holiday gifts switch turns on");
        Install(ServerRateKeys.WeekendExperienceClaim, 0m);
        Check.True(!VillagersSwitches.WeekendClaimEnabled, "weekend claim switch turns off");
        ServerRates.ResetToDefaults();
    }

    private static void CheckWeekendLever()
    {
        ServerRates.ResetToDefaults();
        // 2026-11-02 Monday, 2026-11-07 Saturday, 2026-11-08 Sunday.
        Check.True(!WeekendExperienceClaimRules.IsWeekend(new DateOnly(2026, 11, 2)), "Monday is closed by default");
        Check.True(WeekendExperienceClaimRules.IsWeekend(new DateOnly(2026, 11, 7)), "Saturday is open");
        Check.True(WeekendExperienceClaimRules.IsWeekend(new DateOnly(2026, 11, 8)), "Sunday is open");
        Install(ServerRateKeys.WeekendExperienceAnyDay, 1m);
        Check.True(WeekendExperienceClaimRules.IsWeekend(new DateOnly(2026, 11, 2)), "any-day lever opens Monday");
        ServerRates.ResetToDefaults();
        Check.True(!WeekendExperienceClaimRules.IsWeekend(new DateOnly(2026, 11, 4)), "lever reset closes Wednesday");
    }

    private static void CheckBooks()
    {
        var athens = VillageLeaderRules.BooksFor(VillageLeaderRules.AthensCamp);
        var sparta = VillageLeaderRules.BooksFor(VillageLeaderRules.SpartaCamp);
        Check.True(
            athens.Select(static b => b.BookItemId).SequenceEqual([5800, 5802]) &&
            athens.Select(static b => b.SkillId).SequenceEqual([3064, 4904]),
            "Admes (Athens) re-issues Marathon Portal 5800/3064 and Riding 5802/4904");
        Check.True(
            sparta.Select(static b => b.BookItemId).SequenceEqual([5801, 5802]) &&
            sparta.Select(static b => b.SkillId).SequenceEqual([3065, 4904]),
            "Stefanos (Sparta) re-issues Peloponnesus Portal 5801/3065 and Riding 5802/4904");
        Check.Equal(0, VillageLeaderRules.BooksFor(7).Count, "unknown camp gets nothing");
        Check.True(
            VillageLeaderRules.IsVillageLeader("Athens_057") &&
            VillageLeaderRules.IsVillageLeader("Sparta_057") &&
            !VillageLeaderRules.IsVillageLeader("Athens_058"),
            "only the two Village Leaders");
        Check.Equal(40, VillageLeaderRules.MinimumLevel, "re-issue from level 40 (wiki-08)");
    }

    private static void CheckFestivalPages()
    {
        var empty = Enumerable.Repeat(-1, 18).ToArray();
        var claim = Enumerable.Repeat(-1, 18).ToArray();
        claim[0] = 501;
        Check.Equal(string.Empty, FestivalEnvoyRules.ReadPath(empty), "empty path");
        Check.Equal("501", FestivalEnvoyRules.ReadPath(claim), "weekend claim path");
        Check.Equal(50_001, FestivalEnvoyRules.ExperienceLine(50), "EXP line is n*1000+1");
        Check.Equal(3_002, FestivalEnvoyRules.TalentLine(3), "Talent line is n*1000+2");
        Check.Equal(2_000_000_001, FestivalEnvoyRules.ExperienceLine(int.MaxValue), "EXP line is capped below Int32");
        var gift = new FestivalGiftDay(new DateOnly(2026, 12, 25), 13, 30, 100_000, 2, 4165, 1);
        Check.True(
            FestivalEnvoyRules.GrantedPage(gift).SequenceEqual([110, 100_000_001, 2_002, 101]),
            "granted page: obtained text, EXP, Talent, B-Gold Bag line");
        Check.True(
            FestivalEnvoyRules.GrantedPage(gift with { TalentPoints = 0, ItemId = 0 })
                .SequenceEqual([110, 100_000_001]),
            "granted page omits empty parts");
        Check.True(
            FestivalEnvoyRules.GrantedPage(gift).All(static id => id % 1000 is not (3 or 4)),
            "no blueprint/scroll family ids (%1000 == 3/4) are ever sent");
        Check.Equal(213, FestivalEnvoyRules.IntroText(13), "Christmas intro text");
        Check.Equal(313, FestivalEnvoyRules.NextText(13), "Christmas next-festival text");
        Check.Equal(15, FestivalEnvoyRules.FestivalNames.Length, "15 client festivals");
        Check.True(FestivalEnvoyRules.IsTooLate(new TimeOnly(23, 55)) &&
            !FestivalEnvoyRules.IsTooLate(new TimeOnly(23, 54, 59)), "claims close at 23:55");
        Check.True(FestivalEnvoyRules.IsFestivalEnvoy("Sparta_084") &&
            !FestivalEnvoyRules.IsFestivalEnvoy("Sparta_092"), "only the Festival Envoy");
    }

    private static void CheckReplyFrame()
    {
        var frame = PacketBuilder.CapturedNpcFunctionActionResponse(5081u, 28, 110, 100_000_001, 2_002, 101);
        Check.Equal(96, frame.Length, "festival pages use the captured 96-byte 10070 frame");
        Check.Equal(110, BitConverter.ToInt32(frame, 12), "first entry");
        Check.Equal(101, BitConverter.ToInt32(frame, 24), "fourth entry");
        Check.Equal(0, BitConverter.ToInt32(frame, 28), "unused entries are zero");
    }
}
