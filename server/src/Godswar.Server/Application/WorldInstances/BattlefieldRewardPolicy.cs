using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>How a battlefield round ended for one faction (same numbers as the farm's outcome).</summary>
internal enum BattlefieldOutcome : short
{
    Lost = 0,
    Won = 1,
    Draw = 2
}

/// <summary>What a participant is owed when a Pindus or Ni Mini round settles.</summary>
/// <param name="Experience">The level package's EXP ("Faction Rewards" on the client: 800 x level for a win).</param>
/// <param name="TalentPoints">The level package's talent points.</param>
/// <param name="Reputation">Reputation (paid into the honour balance, see the README).</param>
/// <param name="KillExperience">Pindus points package: kill points x level x factor.</param>
/// <param name="HolyExperience">Pindus points package: Holy Water points x level x factor.</param>
internal readonly record struct BattlefieldReward(
    int Experience, int TalentPoints, int Reputation, int KillExperience, int HolyExperience);

/// <summary>
/// The shared battlefield round rules for Pindus Mountains and Ni Mini Valley: which round a moment belongs to,
/// who won, what each participant is owed, which titles the rankings earn, and the numbers the client's own
/// <c>NpcFunWar.lua</c> needs to draw each Battlefield Awarder answer.
/// </summary>
/// <remarks>
/// Rules are the IGG guide's (the admin 2026-11-03: IGG wins over the wiki). Pindus: guide acid=123. Ni Mini: acid=182.
/// Anything the guide does not give a number for is marked GENERATED here and in the README.
/// </remarks>
internal static class BattlefieldRewardPolicy
{
    public const short Pindus = 1;
    public const short NiMini = 2;

    public const int PindusMapId = 38;
    public const int NiMiniMapId = 34;

    /// <summary>The Pindus Dragon (client WarField/Monster.ini) and its IGG worth: 1,000 points to the last hitter's faction.</summary>
    public const string PindusDragonTemplateKey = "B_bossA_dragon_002";
    public const int PindusDragonPoints = 1_000;

    /// <summary>Holy Water (client item 4522, "AutoItem5"; the Quartermaster's text: one bottle at a time).</summary>
    public const int HolyWaterItemId = 4522;

    /// <summary>
    /// IGG acid=123 Holy Water points: 30 at the own faction's Fortune Altar, 100 at the hostile one; 40 / 120 instead
    /// when the other side leads by more than 30% of this side's points.
    /// </summary>
    public static int HolyWaterPoints(bool hostileAltar, long ownPoints, long otherPoints)
    {
        var behind = otherPoints - ownPoints > 0.3m * Math.Max(0, ownPoints) && otherPoints > ownPoints;
        return hostileAltar ? (behind ? 120 : 100) : (behind ? 40 : 30);
    }

    /// <summary>Client title ids (editor_title_catalog). IGG names differ, see the README.</summary>
    public const int PindusTopKillerTitleId = 5005;   // IGG "Natural Born Killer", 1st by kill points
    public const int PindusKillerTitleId = 5006;      // IGG "Battlefield Slayer", 2nd-5th by kill points
    public const int PindusTopScorerTitleId = 5007;   // IGG "Score King", 1st by non-kill points
    public const int PindusScorerTitleId = 5008;      // IGG "High Scorer", 2nd-5th by non-kill points
    public const int NiMiniAthensTitleId = 5015;      // client "Pride of Athens"; IGG "Gifted Athens Youth"
    public const int NiMiniSpartaTitleId = 5016;      // client "Pride of Sparta"; IGG "Gifted Sparta Youth"

    public const int TitleRankLimit = 5;

    /// <summary>The battlefield whose scoring a map carries, or 0.</summary>
    public static short ForMap(int mapId) => mapId switch
    {
        PindusMapId => Pindus,
        NiMiniMapId => NiMini,
        _ => 0
    };

    public static BattlefieldDestinationKind ScheduleKind(short battlefield) =>
        battlefield == Pindus ? BattlefieldDestinationKind.Pindus : BattlefieldDestinationKind.NiMiniLower;

    public static short ForDestination(BattlefieldDestinationKind kind) => kind switch
    {
        BattlefieldDestinationKind.Pindus => Pindus,
        BattlefieldDestinationKind.NiMiniLower or BattlefieldDestinationKind.NiMiniUpper => NiMini,
        _ => 0
    };

    /// <summary>
    /// The round the moment <paramref name="now"/> belongs to, or null when the battlefield is closed. A round is the
    /// contiguous open period around <paramref name="now"/> as <see cref="BattlefieldSchedulePolicy"/> sees it, so the
    /// compiled windows, the rates' shift/length and editor windows all give the same answer the transporter gives.
    /// With the "always open" rate on there is no window, so the round is the realm day (GENERATED, for testing).
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? CurrentRound(
        short battlefield, RealmCalendar calendar, DateTimeOffset now, Func<DateTimeOffset, bool>? isOpen = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var kind = ScheduleKind(battlefield);
        var rates = ServerRates.Current;
        var alwaysOpen = battlefield == Pindus
            ? rates.Absolute(ServerRateKeys.PindusAlwaysOpen) == 1
            : rates.Absolute(ServerRateKeys.NiMiniAlwaysOpen) == 1;
        if (alwaysOpen && isOpen is null)
        {
            var day = calendar.GetDay(now);
            var start = calendar.GetStartOfDay(day);
            return (start, calendar.GetStartOfDay(day.AddDays(1)));
        }

        isOpen ??= instant => BattlefieldSchedulePolicy.IsOpen(kind, calendar, instant);
        if (!isOpen(now))
        {
            return null;
        }

        // Windows start and end on whole minutes. The scan covers the longest window the rates and the event editor
        // allow (1,440 minutes) each way. A window still open past that (back-to-back or overlapping windows) is
        // anchored to the realm day it is in, as "always open" is, so its identity can never slide minute by minute.
        // (The store also keeps scoring an unsettled round that already covers the moment, so a mid-window change
        // of the shift / length rates does not start a second round either.)
        var minute = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        var first = minute;
        var step = 0;
        for (; step < MaximumWindowMinutes && isOpen(first.AddMinutes(-1)); step++)
        {
            first = first.AddMinutes(-1);
        }
        var startCapped = step == MaximumWindowMinutes;
        var end = minute.AddMinutes(1);
        for (step = 0; step < MaximumWindowMinutes && isOpen(end); step++)
        {
            end = end.AddMinutes(1);
        }
        if (startCapped || step == MaximumWindowMinutes)
        {
            var day = calendar.GetDay(now);
            return (calendar.GetStartOfDay(day), calendar.GetStartOfDay(day.AddDays(1)));
        }
        return (first, end);
    }

    /// <summary>The faction that won (0 Sparta, 1 Athens) or null for a draw.</summary>
    public static byte? Winner(long spartaPoints, long athensPoints) =>
        spartaPoints == athensPoints ? null : spartaPoints > athensPoints ? (byte)0 : (byte)1;

    public static BattlefieldOutcome OutcomeFor(byte faction, long spartaPoints, long athensPoints) =>
        Winner(spartaPoints, athensPoints) is not { } winner
            ? BattlefieldOutcome.Draw
            : winner == faction ? BattlefieldOutcome.Won : BattlefieldOutcome.Lost;

    /// <summary>
    /// IGG acid=123 Pindus rewards. Individual: win 800 Rep, talent = level, EXP 800 x level; loss 400 Rep,
    /// talent 0.5 x level, EXP 400 x level. Faction: EXP = (Holy Water points + kill points) x level x 0.06 (win) or
    /// 0.04 (loss). A tie: "each faction will receive half of the reward designated for the winning faction"
    /// (so 400 Rep, 0.5 x level talent, 400 x level EXP and x 0.03). Fractions round down.
    /// </summary>
    public static BattlefieldReward PindusReward(int level, BattlefieldOutcome outcome, long killPoints, long holyPoints)
    {
        var l = Math.Max(1, level);
        var (expPerLevel, reputation, talent, factor) = outcome switch
        {
            BattlefieldOutcome.Won => (800, 800, l, 0.06m),
            BattlefieldOutcome.Lost => (400, 400, l / 2, 0.04m),
            _ => (400, 400, l / 2, 0.03m)
        };
        return new BattlefieldReward(
            checked(expPerLevel * l), talent, reputation,
            ClampToInt(Math.Max(0, killPoints) * l * factor),
            ClampToInt(Math.Max(0, holyPoints) * l * factor));
    }

    /// <summary>
    /// IGG acid=182 Ni Mini rewards: 600 Rep to every player of the winning faction, 200 to the losing one.
    /// A draw is not in the guide: GENERATED 200 Rep, matching the client's own draw line (NF_L0_W210).
    /// </summary>
    public static BattlefieldReward NiMiniReward(BattlefieldOutcome outcome) =>
        new(0, 0, outcome == BattlefieldOutcome.Won ? 600 : 200, 0, 0);

    /// <summary>The longest window, in minutes, the rates (event.battlefield_minutes) and the event editor allow.</summary>
    public const int MaximumWindowMinutes = 1_440;

    /// <summary>
    /// Personal points per enemy kill (Pindus and Ni Mini). Pindus: a reading of IGG "5 points and 1 contribution",
    /// matching the client's own Crier text (NF_L0_W212/W232: 1 point to you, 5 to your faction).
    /// </summary>
    public const int PersonalKillPoints = 1;

    // BATTLEFIELD2: the Pindus level band (the admin 2026-11-03, round 2 answer 4: 60-200; IGG acid=123 says 60-130, the
    // stock reborn transporter 31-120). battlefield.pindus_band = 0 restores the stock band and scores every level.
    public const int PindusBandMinimumLevel = 60;
    public const int PindusBandMaximumLevel = 200;

    public static bool PindusBandEnabled =>
        ServerRates.Current.Absolute(ServerRateKeys.BattlefieldPindusBand) == 1;

    /// <summary>Whether a character of this level may score on Pindus (always true with the band switch off).</summary>
    public static bool PindusLevelEligible(int level)
    {
        // FINALISE2: an editor band (Gates table) decides who scores too, so scoring and the gate cannot disagree.
        if (!PindusBandEnabled && !Npc.NpcContentOverrides.Current.LevelBands.ContainsKey(Npc.LevelBandGates.Pindus))
        {
            return true;
        }

        var band = Godswar.Server.Application.Events.GateBands.Pindus();
        return level >= band.Minimum && level <= band.Maximum;
    }

    /// <summary>
    /// Faction points per Pindus enemy kill (IGG: 5; battlefield.pindus_kill_points). A flat rate: the victim's
    /// level is not part of it and only breaks ties in the kill ranking (GENERATED tie-break, see the README).
    /// </summary>
    public static int PindusKillPoints() =>
        (int)Math.Clamp(ServerRates.Current.Absolute(ServerRateKeys.BattlefieldPindusKillPoints), 0, 1_000);

    /// <summary>
    /// The last moment the reward of a round can be claimed. Pindus, IGG acid=123: on the day of the battle (the client
    /// line 1757); the captured Awarder text says until 11:55pm. Ni Mini: the guide (acid=182) only says to claim
    /// "straight after the battle", no deadline; the same battle-day rule is applied (GENERATED, borrowed from Pindus).
    /// A positive claim-hours rate replaces it (GENERATED).
    /// </summary>
    public static DateTimeOffset ClaimDeadline(RealmCalendar calendar, DateTimeOffset roundStart, DateTimeOffset roundEnd)
    {
        var hours = ServerRates.Current.Absolute(ServerRateKeys.BattlefieldClaimHours);
        if (hours > 0)
        {
            return roundEnd.AddHours(hours);
        }
        var day = calendar.GetDay(roundStart);
        var deadline = calendar.GetStartOfDay(day).AddHours(23).AddMinutes(55);
        // A window that ends after 23:55 (a shifted one) still leaves the rest of its own day.
        return deadline > roundEnd ? deadline : calendar.GetStartOfDay(day.AddDays(1)).AddHours(23).AddMinutes(55);
    }

    /// <summary>The Pindus title for a kill-ranking place (1-5), or 0.</summary>
    public static int PindusKillTitle(int rank) =>
        rank == 1 ? PindusTopKillerTitleId : rank is >= 2 and <= TitleRankLimit ? PindusKillerTitleId : 0;

    /// <summary>The Pindus title for a non-kill-ranking place (1-5), or 0.</summary>
    public static int PindusTaskTitle(int rank) =>
        rank == 1 ? PindusTopScorerTitleId : rank is >= 2 and <= TitleRankLimit ? PindusScorerTitleId : 0;

    public static int NiMiniTitle(byte faction) => faction == 1 ? NiMiniAthensTitleId : NiMiniSpartaTitleId;

    private static int ClampToInt(decimal value) => (int)Math.Clamp(Math.Floor(value), 0m, 10_000_000m);
}

/// <summary>
/// The numbers the stock <c>NpcFunWar.lua</c> (function 3) draws for each Battlefield Awarder answer. The client
/// keeps a page counter (Index) that advances with each reply, and a number only draws inside its own Index branch,
/// so each answer below is listed with the Index it is drawn at. Amounts are encoded into the number and the client
/// prints them itself: <c>level x 200 + 21</c> draws "Faction Rewards: 800 x level EXP, 800 Reputation, level
/// Talent" and so on.
/// </summary>
internal static class BattlefieldAwarderReplies
{
    public const int FunctionNumber = 3;

    // Index 1/2: the opening menu and the battlefields' own pages (captured 2026-09-28, unchanged).
    public const int PindusPage = 1;
    public const int NiMiniPage = 2;
    public const int FarmPage = 3;
    public const int ClaimFaction = 173;
    public const int ClaimTitle = 174;
    public const int ClaimIndividual = 175;
    public const int Statistics = 176;
    public const int NiMiniBracket = 1967;
    public const int FarmFactionReward = 71;
    public const int FarmFirstPlace = 72;
    public const int FarmKelsis = 73;
    public const int MostKills = 3001;
    public const int BestScore = 3002;
    public const int TopHealer = 3003;
    public const int TopFiveScores = 11763;
    public const int TopFiveKills = 11764;
    public const int TopFiveHeals = 11765;
    public const int FactionPoints = 11766;

    // Index 3 lines.
    public const int NotEntered = 1755;            // "You didn't enter the battlefield so you cannot claim the rewards."
    public const int AlreadyClaimed = 1756;        // "You have already claimed rewards."
    public const int ClaimOnBattleDay = 1757;      // "Please claim your rewards on the day when you entered the battleground."
    public const int NoContribution = 11761;       // "...not made any contributions to use in claiming rewards."
    public const int PindusFeat = 11762;           // "Congratulations on your feat of strength on Pindus Mountain!"
    public const int FactionWon = 1963;
    public const int FactionLost = 1964;
    public const int FactionDraw = 1965;
    public const int FourHundredReputation = 1760;  // a fixed "400 Reputation" line
    public const int FarmNotJoined = 201;
    public const int FarmAlreadyClaimed = 204;
    public const int FarmNotFirst = 205;
    public const int FarmFirst = 206;
    public const int FarmNoData = 207;
    public const int FarmDidNotJoin = 210;

    // Index 4 lines.
    public const int TitleClaimed = 3;             // "You've claimed the title!"
    public const int ReputationClaimed = 4;        // "You've claimed the Reputation"
    public const int TitleObtained = 3012;         // "Successfully obtained"
    public const int OnlyTopFive = 3014;
    public const int OnlyBravest = 3015;
    public const int OnlyTopHealers = 3016;
    public const int NotJoinedOrError = 1761;
    public const int NoStatistics = 11771;
    public const int AthensWon = 600;
    public const int SpartaWon = 601;
    public const int AthensLost = 200;
    public const int SpartaLost = 201;
    public const int Draw = 1200;

    /// <summary>Index 3, Pindus 173: the level package.</summary>
    public static int[] PindusFactionReward(BattlefieldOutcome outcome, int level, int talent, int experience) =>
        outcome switch
        {
            BattlefieldOutcome.Won => [FactionWon, Encode200(level, 21)],
            BattlefieldOutcome.Lost => [FactionLost, Encode200(level, 22)],
            _ => [FactionDraw, FourHundredReputation, Encode200(talent, 29), Encode200(experience, 27)]
        };

    /// <summary>Index 3, Pindus 175: the points package (kill EXP and Holy Water EXP lines).</summary>
    public static int[] PindusPointsReward(int killExperience, int holyExperience) =>
        [PindusFeat, Encode200(killExperience, 23), Encode200(holyExperience, 24)];

    /// <summary>Index 4, Ni Mini 173: the faction's reputation line.</summary>
    public static int NiMiniReputationLine(BattlefieldOutcome outcome, byte faction) => outcome switch
    {
        BattlefieldOutcome.Won => faction == 1 ? AthensWon : SpartaWon,
        BattlefieldOutcome.Lost => faction == 1 ? AthensLost : SpartaLost,
        _ => Draw
    };

    /// <summary>Index 3, farm 71: <c>level x 1000 + 14</c> (won or draw) or <c>+ 15</c> (lost).</summary>
    public static int FarmReward(int level, bool lost) => checked(Math.Clamp(level, 1, 2_000) * 1_000 + (lost ? 15 : 14));

    /// <summary>Index 4 ranking rows: <c>100000 + points x 100 + place</c> (places 1-5, kills 11-15).</summary>
    public static int RankingRow(long value, int place) =>
        checked(100_000 + (int)Math.Clamp(value, 0, 20_000_000) * 100 + place);

    /// <summary>Index 4 "Your current score:" line.</summary>
    public static int OwnScore(long points) => checked((int)Math.Clamp(points, 0, 20_000_000) * 100 + 17);

    /// <summary>Index 4 faction points: Athens (21) and Sparta (22).</summary>
    public static int[] FactionPointRows(long athens, long sparta) =>
        [RankingRow(athens, 21), RankingRow(sparta, 22)];

    private static int Encode200(int value, int line) => checked(Math.Clamp(value, 0, 10_000_000) * 200 + line);

    /// <summary>
    /// The level the farm reward was computed for, recovered from its stored character EXP
    /// (10,000 + 50 x level^2, halved for a loss) so the client's own formula prints the paid amounts.
    /// </summary>
    public static int FarmLevelFromExperience(int characterExperience, bool lost)
    {
        var full = lost ? characterExperience * 2L : characterExperience;
        var squared = Math.Max(0, (full - 10_000) / 50.0);
        return Math.Max(1, (int)Math.Round(Math.Sqrt(squared)));
    }
}

/// <summary>
/// BATTLEFIELD2: the amounts the Pindus Awarder lines print. The EXP and talent are paid through the Event NPC EXP &amp;
/// Talent rate (exp.events, x10 on live), while the client prints the x1 base numbers the reply encodes. With
/// battlefield.show_paid_amounts = 1 the reply encodes what is paid instead: the EXP lines (+23, +24, +27) and the
/// draw talent line (+29) carry the amount itself; the faction lines (+21 win, +22 loss) carry a number N the Lua
/// turns into 800 x N (win) or 400 x N (loss, after the client edit) EXP and N or N/2 talent, so N is only raised
/// above the level when both printed numbers then equal the paid ones exactly (otherwise the base line stays).
/// The Reputation shown stays the client's fixed 800/400.
/// </summary>
internal static class BattlefieldPaidAmounts
{
    public static bool Enabled =>
        ServerRates.Current.Absolute(ServerRateKeys.BattlefieldShowPaidAmounts) == 1;

    /// <summary>The EXP <c>PayZeusGiftProgressionAsync</c> pays for this base amount.</summary>
    public static int Experience(int baseExperience) =>
        ServerRates.Current.Multiply(
            ServerRateKeys.EventExperience,
            Math.Max(0, baseExperience),
            Godswar.Server.Application.Rewards.MonsterDeathRewardCommandEnvelope.MaximumAwardedExperience);

    /// <summary>The talent points paid for this base number of points (paid as talent EXP, scaled the same way).</summary>
    public static int TalentPoints(int basePoints)
    {
        var perPoint = Godswar.Server.Application.ZeusGift.ZeusGiftPolicy.TalentExperiencePerPoint;
        return ServerRates.Current.Multiply(
            ServerRateKeys.EventExperience,
            checked(Math.Max(0, basePoints) * perPoint),
            Godswar.Server.Application.Rewards.MonsterDeathRewardCommandEnvelope.MaximumAwardedTalentExperience) / perPoint;
    }

    /// <summary>The N of the +21 / +22 faction line (see the class remarks).</summary>
    public static int FactionLineLevel(BattlefieldOutcome outcome, int level, int experience, int talentPoints)
    {
        if (!Enabled || outcome is not (BattlefieldOutcome.Won or BattlefieldOutcome.Lost))
        {
            return level;
        }
        var perLevel = outcome == BattlefieldOutcome.Won ? 800 : 400;
        var paidExperience = Experience(experience);
        if (paidExperience % perLevel != 0)
        {
            return level;
        }
        var n = paidExperience / perLevel;
        var shownTalent = outcome == BattlefieldOutcome.Won ? n : n / 2;
        return n is > 0 and <= 10_000_000 && shownTalent == TalentPoints(talentPoints) ? n : level;
    }

    /// <summary>The kill and Holy Water EXP lines; their sum is exactly the one combined payment.</summary>
    public static (int Kill, int Holy) PointsLines(int killExperience, int holyExperience)
    {
        if (!Enabled)
        {
            return (killExperience, holyExperience);
        }
        var kill = Experience(killExperience);
        return (kill, Experience(checked(killExperience + holyExperience)) - kill);
    }
}
