using Godswar.Server.Application.Gates; // FINALISE2
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Realms;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>How a Lelantine Farm round ended for a faction.</summary>
internal enum FarmRoundOutcome : short
{
    Lost = 0,
    Won = 1,
    Draw = 2
}

/// <summary>What one scorer is owed when a round settles.</summary>
internal readonly record struct FarmRoundReward(int PetExperience, int CharacterExperience, int TalentPoints);

/// <summary>
/// The Lelantine Farm round clock and reward formulas. The wiki page (wiki.godsarena.online, "The Defense of
/// Lelantine Farm") gives the window (Saturday 22:45-23:30), the winner formulas (pet exp 140,000 + 5,000 per
/// level, character exp 10,000 + 50 per level squared, talent 16 + 0.2 per level), half for the loser and the full
/// winner reward for both on a draw.
/// FINALISE2 (the admin F-03/F-04): the window is REALM time (Australia/Brisbane = AEST on live; was a fixed Asia/Manila
/// clock, which put the round on Sunday 00:45 AEST). It is the gate's own window (GateCatalog
/// battlefield.lelantine_farm, editor rows in event_schedule_overrides activity "LelantineFarm"), so the transporter,
/// the round scoring and the end-of-round send-home (F-66) read one clock.
/// </summary>
internal static class LelantineFarmRoundPolicy
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(45);

    /// <summary>The latest round window that began at or before <paramref name="instant"/>, as UTC instants.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) LatestWindow(DateTimeOffset instant, RealmCalendar? calendar = null) // FINALISE2
    {
        calendar ??= RealmClock.Calendar; // FINALISE2
        var realmNow = calendar?.ToRealmTime(instant).DateTime ?? instant.UtcDateTime;
        var windows = GateRules.RoundWindows(LevelBandGates.LelantineFarm);
        var latest = GateRules.LatestWindow(windows, realmNow) ??
            GateRules.LatestWindow(GateCatalog.Find(LevelBandGates.LelantineFarm)!.CompiledWindows!, realmNow)!.Value;
        DateTimeOffset ToUtc(DateTime local) => calendar?.FromRealmWallClock(local) ??
            new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero);
        return (ToUtc(latest.Start), ToUtc(latest.End));
    }

    public static bool IsOpen(DateTimeOffset instant, RealmCalendar? calendar = null) // FINALISE2
    {
        var (start, end) = LatestWindow(instant, calendar); // FINALISE2
        return instant >= start && instant < end;
    }

    /// <summary>The faction that won (0 Sparta, 1 Athens) or null for a draw.</summary>
    public static byte? Winner(long spartaPoints, long athensPoints) =>
        spartaPoints == athensPoints ? null : spartaPoints > athensPoints ? (byte)0 : (byte)1;

    public static FarmRoundOutcome OutcomeFor(byte faction, long spartaPoints, long athensPoints) =>
        Winner(spartaPoints, athensPoints) is not { } winner
            ? FarmRoundOutcome.Draw
            : winner == faction ? FarmRoundOutcome.Won : FarmRoundOutcome.Lost;

    /// <summary>Winner and draw get the full formula, the losing faction half (rounded down).</summary>
    public static FarmRoundReward RewardFor(int characterLevel, FarmRoundOutcome outcome)
    {
        var level = Math.Max(1, characterLevel);
        var pet = 140_000 + level * 5_000;
        var exp = 10_000 + level * level * 50;
        var talent = (int)Math.Round(16 + level * 0.2, MidpointRounding.AwayFromZero);
        return outcome == FarmRoundOutcome.Lost
            ? new(pet / 2, exp / 2, talent / 2)
            : new(pet, exp, talent);
    }
}

/// <summary>
/// Ninto's free wooden tools (the wiki page and the IGG guide: 40 wooden tools once the battlefield opens, then 5 more
/// whenever the 40 have run out). The page puts the handout on the NPC Ninto; his click path was never captured and his
/// dialogue is frozen, hash-checked content, so the server hands the tools out automatically instead (GENERATED): on the
/// farm, 40 the first time in a round window, then 5 whenever the character holds no wooden tool, at most once a minute.
/// </summary>
internal static class LelantineFarmToolPolicy
{
    public const int WoodenToolItemId = 10080;
    public const int FirstHandout = 40;
    public const int TopUp = 5;
    public const int MaxStack = 99;
    public static readonly TimeSpan TopUpInterval = TimeSpan.FromSeconds(60);
    public const int MinimumLevel = 31;
}
