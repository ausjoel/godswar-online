using Godswar.Server.Application.Events;
using Godswar.Server.Application.Gates;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Troy;

// W2 F-27 TROY: the Trojan Expedition (map 44), its coordinator and its awarders. Pure rules only; the handler is
// Game/GameClientHandler.Troy.cs. Sources and what is GENERATED: Development/troy/README.md.
//
// Every number the client draws is the client's own encoding, read from UI/XML/NpcFun/NpcFunTroy.lua:
//   ranking line  score*1000 + i   (i 1-10 = guild rank, 11 = "Your guild's score", 12 = "Your score")
//   guild reward  silver + 13      (the client prints silver/100 as guild gold, so gold = score*10, silver = score*1000)
//   personal      pills*1000 + 16  ("entitled to N Exp pills")
// The two reward formulas are also IGG's (guide acid 208, 2010): guild Gold = score x 10, guild Silver = score x 1,000,
// 1 personal point = 1 Exp Pill; kills score 1 / 5 / 10 for a normal / elite / boss.
internal static class TroyEvent
{
    public const int Function = 67; // NPC_FLAG_SYS_TROY_HORT
    public const short MapId = 44;
    public const int MinimumLevel = 50; // NF_L0_TR2802 "You have to be Level 50+."
    public const string GateKey = "event.troy";

    /// <summary>Exp Pill (4174, Overlap 99, BindType 1): the personal reward (NF_L0_TR017 "Exp pills").</summary>
    public const int ExpPillItem = 4174;

    /// <summary>"Top Greek Guild" (editor_title_catalog 5123): "The best scoring guild in the Trojan Expedition. Lasts a week."</summary>
    public const int TopGuildTitle = 5123;

    public const int TitleDays = 7;

    /// <summary>The beach landing: Address.ini "To the Beach" (137, -159) of map 44 (MapTemplateSeed). GENERATED as an arrival.</summary>
    public const float ArrivalX = 137f, ArrivalZ = -159f;

    public const int HpRecoverySilver = 5_000; // NF_L0_TR105 "HP recovery (5,000 silver)"

    // Page-one buttons (Index 1) and the replies they open.
    public const int RewardsEntry = 101, HpRecoveryEntry = 105, BackdropEntry = 106, MainCityEntry = 321;
    public const int CheckScore = 201, ClaimGuild = 202, ClaimPersonal = 203;
    public const int NotEnoughSilver = 204, HpMpFull = 205, Backdrop = 206;
    public const int NoGuild = 301, GuildClaimed = 302, NotEntitled = 303, PersonalClaimed = 304, BagFull = 305,
        TopGuildLine = 306, ClaimDeadline = 307, NotDirector = 308, NoRankings = 311;

    /// <summary>The Event Transporter's "*To the Trojan Expedition" (NF_L0_TR800) and its two refusals.</summary>
    public const int TransporterEntry = 900, TransporterClosed = 2801, TransporterLevel = 2802;

    public const string CoordinatorSwitch = "troy.coordinator";
    public const string EntrySwitch = "troy.entry";
    public const string AwardsSwitch = "troy.awards";
    public const string NormalPoints = "troy.points_normal";
    public const string ElitePoints = "troy.points_elite";
    public const string BossPoints = "troy.points_boss";
    public const string RewardPercent = "troy.reward_percent";

    public static bool CoordinatorOn => ServerRates.Current.Absolute(CoordinatorSwitch) == 1;
    public static bool EntryOn => ServerRates.Current.Absolute(EntrySwitch) == 1;
    public static bool AwardsOn => ServerRates.Current.Absolute(AwardsSwitch) == 1;

    public const string Category = "Trojan Expedition (Troy event)";

    private static ServerRateDefinition Switch(string key, string label, decimal value, int order, string text) =>
        new(key, Category, label, "0/1", ServerRateKind.Absolute, value, 0m, 1m, 1m, order,
            "Game/GameClientHandler.Troy.cs", text);

    private static ServerRateDefinition Points(string key, string label, decimal value, int order) =>
        new(key, Category, label, "points", ServerRateKind.Absolute, value, 0m, 1000m, 1m, order,
            "Application/Troy/TroyEvent.cs", "Personal and guild points for one kill on map 44 while the event is open (IGG 2010: 1 / 5 / 10).");

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        Switch(CoordinatorSwitch, "Trojan War Coordinator answers (HP recovery, backdrop, main city, rewards)", 1m, 1,
            "1 = the Trojan War Coordinator (Athens_125 / Sparta_125) answers its buttons: HP and MP recovery for 5,000 " +
            "Silver, the backdrop story, back to the main city, and the reward pages of the awarder. 0 = page only, as before."),
        Switch(EntrySwitch, "Event Transporter: To the Trojan Expedition (opening hours, Lv50+)", 0m, 2,
            "1 = the capital Event Transporter shows '*To the Trojan Expedition' and sends Lv50+ players to the Troy beach " +
            "while the event.troy gate is open (Thursday 20:00-21:00 AEST, Gates page); players still in Troy when it " +
            "closes go back to their capital. OFF by default: Troy has no monsters yet (GM spawns only). 0 = the captured menu."),
        Switch(AwardsSwitch, "Event Awarders: Troy scores, guild and personal rewards", 1m, 3,
            "1 = kills on map 44 while the event is open score for the player and their guild; the Event Awarders " +
            "(Athens_124 / Sparta_124) show the guild ranking and pay guild Silver / Gold, Exp Pills and the 'Top Greek " +
            "Guild' title, claimable from the end of the event until 23:55 the same day. 0 = the captured pages only."),
        Points(NormalPoints, "Points: normal Trojan", 1m, 4),
        Points(ElitePoints, "Points: elite Trojan", 5m, 5),
        Points(BossPoints, "Points: Trojan hero (boss)", 10m, 6),
        new(RewardPercent, Category, "Reward size", "%", ServerRateKind.Absolute, 100m, 0m, 1000m, 5m, 7,
            "Application/Troy/TroyEvent.cs",
            "100 = IGG's rewards: 1 Exp Pill per personal point, guild Silver = guild score x 1,000 and guild Gold = guild " +
            "score x 10 (one value: the client prints the gold as silver / 100).")
    ];

    /// <summary>Thursday 20:00-21:00 realm time: NF_L0_TR2801 and the Event Transporter's timetable (IGG 2010: 20:00-21:30).</summary>
    public static IReadOnlyList<EventWindow> CompiledWindows { get; } = [new(4, 20 * 60, 60)];

    public static GateDefinition Gate { get; } = new(GateKey, "Event", "Trojan Expedition (map 44)",
        "Event Transporter 900 (troy.entry = 1)", MinimumLevel, GateCatalog.LevelCap, CompiledWindows,
        "Thu 20:00-21:00", false, true,
        "NF_L0_TR2801 'Thursdays 8:00pm - 9:00pm'; NF_L0_TR2802 'Level 50+'",
        "W2 F-27: the client's own hours; IGG 2010 had 20:00-21:30, 2011 22:00-23:00. Kills score only while open.");

    /// <summary>Whether the event is running (entry, scoring).</summary>
    public static bool IsOpen(Realms.RealmCalendar calendar, DateTimeOffset instant) =>
        GateSchedules.IsOpen(GateKey, calendar, instant);

    /// <summary>
    /// The event day a kill at <paramref name="realmNow"/> scores for: the realm date its window started (a window may
    /// cross midnight), or the realm date itself when the gate is forced open outside a window.
    /// </summary>
    public static DateOnly ScoringDay(IReadOnlyList<EventWindow> windows, DateTime realmNow)
    {
        var latest = GateRules.LatestWindow(windows, realmNow);
        return latest is { } window && realmNow < window.End
            ? DateOnly.FromDateTime(window.Start)
            : DateOnly.FromDateTime(realmNow);
    }

    /// <summary>
    /// The event day whose rewards may be claimed at <paramref name="realmNow"/>: from the end of the latest window until
    /// 23:55 of the realm day it started ("Prizes can be claimed before 11:55pm on the day of the event"). Null outside
    /// that span. A gate forced to "always" lets today's scores be claimed at any time (test convenience).
    /// </summary>
    public static DateOnly? ClaimDay(IReadOnlyList<EventWindow> windows, DateTime realmNow, bool forcedOpen)
    {
        if (forcedOpen)
        {
            return DateOnly.FromDateTime(realmNow);
        }

        if (GateRules.LatestWindow(windows, realmNow) is not { } window)
        {
            return null;
        }

        var deadline = window.Start.Date.AddHours(23).AddMinutes(55);
        return realmNow >= window.End && realmNow < deadline ? DateOnly.FromDateTime(window.Start) : null;
    }

    public static int PointsFor(bool isElite, bool isBoss) => (int)ServerRates.Current.Absolute(
        isBoss ? BossPoints : isElite ? ElitePoints : NormalPoints);

    private static long Scaled(long value) => value * (long)ServerRates.Current.Absolute(RewardPercent) / 100;

    /// <summary>Guild Silver for a guild score; the client derives the Gold as Silver / 100.</summary>
    public static long GuildSilver(long score) => Math.Min(Scaled(score * 1000), MaxEncodable(13));

    public static long GuildGold(long silver) => silver / 100;

    public static int ExpPills(long points) => (int)Math.Min(Scaled(points), (int.MaxValue - 16) / 1000);

    private static long MaxEncodable(int tail) => (int.MaxValue - tail) / 1000 * 1000;

    /// <summary>One ranking line: <c>score*1000 + slot</c> (slot 1-10 rank, 11 guild score, 12 own score).</summary>
    public static int RankingLine(long score, int slot) =>
        (int)(Math.Clamp(score, 0, (int.MaxValue - 12) / 1000) * 1000 + slot);

    public static int GuildRewardLine(long silver) => (int)(Math.Clamp(silver, 0, MaxEncodable(13)) + 13);

    public static int PersonalRewardLine(int pills) => (int)(Math.Clamp(pills, 0, (int.MaxValue - 16) / 1000) * 1000L + 16);

    /// <summary>
    /// The "Check on the score" page: up to ten guild lines, then the player's guild and own score. A zero score is
    /// sent as <c>1000*0 + slot</c> = the bare slot (1-12), which the client still draws as "N. 0 points"; it never
    /// sends a multiple of 1000. Nothing scored at all = <see cref="NoRankings"/>.
    /// </summary>
    public static int[] RankingPage(IReadOnlyList<long> topGuildScores, long? guildScore, long ownScore)
    {
        if (topGuildScores.Count == 0 && ownScore <= 0)
        {
            return [NoRankings];
        }

        var lines = topGuildScores.Take(10).Select((score, index) => RankingLine(score, index + 1)).ToList();
        if (guildScore is { } guild)
        {
            lines.Add(RankingLine(guild, 11));
        }

        lines.Add(RankingLine(ownScore, 12));
        return [.. lines];
    }
}
