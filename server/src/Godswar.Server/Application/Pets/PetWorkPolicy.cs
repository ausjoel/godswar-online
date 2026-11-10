using System.Collections.Concurrent;

namespace Godswar.Server.Application.Pets;

// PETWORK (F-137, the admin "Build it from Pet_Job.xml"): pet work prices and rewards.
//
// How the table is read (from the client itself, GodsWar.exe 0x6aa8b0, the routine the work window and its
// "can I afford it" check 0x6a0850 both call):
// - The row is the PLAYER's level (player object vf+0x20), PlayerLv1..140.
// - Cost = Money (paying with Silver) or Bijou (paying with Gold) x the hours chosen. The client multiplies by the
//   hours (1/3/8/24), which are exactly Config CostBase="1,3,8,24"; the server uses CostBase.
// - Reward = MoneyGain (Silver food) or BijouGain (Gold food) = "EXP,Talent Points", each x GainBase
//   ("1,3.5,10,32") rounded half up (the client adds 0.5 and truncates). Both go to the PLAYER: the finish
//   message is "%PetName% earned %Exp% Exp and %SkillPoint% Talent Points for you", and the 10266 handler
//   0x69fa70 adds them to the player's EXP (+0x2b4) and Talent Points (+0x7f84).
// - Duration = Config Time="3600,10800,28800,86400" seconds.
// - Stopping early pays 20% of the reward and keeps the cost (PETW_X0_19, PetStopWorkQueryCheck); rounding down
//   is GENERATED.
// - Levels above 140 have no row: the client then shows 0 cost / 0 reward. The server uses row 140 (GENERATED).
internal readonly record struct PetWorkRow(
    int Level,
    int Silver,
    int SilverExperience,
    int SilverTalentPoints,
    int Gold,
    int GoldExperience,
    int GoldTalentPoints);

internal enum PetWorkPayType : byte
{
    Silver = 1, // client field +0x1c = 1 when the Silver box (851005) is ticked
    Gold = 2 // = 2 for the Gold box (851004)
}

internal sealed record PetWorkQuote(
    PetWorkPayType Pay,
    int Hours,
    int Cost,
    long Experience,
    int TalentPoints,
    TimeSpan Duration);

internal static partial class PetWorkTable
{
    public const int MaximumLevel = 140;
    public const int StopSharePercent = 20; // PETW_X0_19: "Unfinished work ... will bring you 20% the pay"

    // Config: Time="3600,10800,28800,86400" CostBase="1,3,8,24" GainBase="1,3.5,10,32"
    private static readonly int[] HourChoices = [1, 3, 8, 24];
    private static readonly int[] TimeSeconds = [3600, 10800, 28800, 86400];
    private static readonly int[] CostBase = [1, 3, 8, 24];
    private static readonly decimal[] GainBase = [1m, 3.5m, 10m, 32m];

    public static int RowCount => Rows.Length;

    /// <summary>The row for a player level; above 140 the last row (GENERATED), below 1 the first.</summary>
    public static PetWorkRow Row(int playerLevel) => Rows[Math.Clamp(playerLevel, 1, Rows.Length) - 1];

    public static bool TryQuote(int playerLevel, byte pay, byte hours, out PetWorkQuote quote)
    {
        quote = null!;
        var index = Array.IndexOf(HourChoices, (int)hours);
        if (index < 0 || pay is not ((byte)PetWorkPayType.Silver or (byte)PetWorkPayType.Gold))
        {
            return false;
        }

        var row = Row(playerLevel);
        var silver = pay == (byte)PetWorkPayType.Silver;
        var cost = (silver ? row.Silver : row.Gold) * CostBase[index];
        var experience = Gain(silver ? row.SilverExperience : row.GoldExperience, GainBase[index]);
        var talent = Gain(silver ? row.SilverTalentPoints : row.GoldTalentPoints, GainBase[index]);
        quote = new PetWorkQuote((PetWorkPayType)pay, hours, cost, experience, checked((int)talent),
            TimeSpan.FromSeconds(TimeSeconds[index]));
        return true;
    }

    /// <summary>The client's own rounding: base x GainBase + 0.5, truncated.</summary>
    private static long Gain(int value, decimal factor) => (long)decimal.Floor(value * factor + 0.5m);

    /// <summary>What an early stop pays: 20% of each part, rounded down (GENERATED).</summary>
    public static (long Experience, int TalentPoints) StopShare(long experience, int talentPoints) =>
        (experience * StopSharePercent / 100, talentPoints * StopSharePercent / 100);
}

/// <summary>
/// PETWORK (F-137): pets that are away working, so every owned-pet record built for the client says "working"
/// (record byte 0x30 = 4, which the owned-list handler 0x69c950 turns into the client's pet state 4).
/// Filled from public.pet_work_state when the owner logs in and cleared when the work ends.
/// </summary>
internal static class PetWorkRegistry
{
    private static readonly ConcurrentDictionary<long, byte> Working = new();

    public static bool IsWorking(long petId) => Working.ContainsKey(petId);

    public static void Mark(long petId) => Working[petId] = 0;

    public static void Clear(long petId) => Working.TryRemove(petId, out _);
}
