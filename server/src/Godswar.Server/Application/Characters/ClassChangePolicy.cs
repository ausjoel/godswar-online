using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Talents;

namespace Godswar.Server.Application.Characters;

/// <summary>
/// CLASSCHANGE: the rules of the Class Shifter (<c>Athens_044</c> / <c>Sparta_044</c>,
/// function 44, client script <c>NpcFunWork.lua</c>), as the IGG retail guide states them
/// (Development/reference-analysis/text/igg-acid=184.txt, "Class Shifting System"):
/// free at level 100 or below, one Class Shift Permit (item 4266) above it, nothing may be
/// worn, the old class's spells are sealed and the new class's unsealed, and the old
/// class's talent levels become talent points.
/// </summary>
internal static class ClassChangePolicy
{
    public const int FunctionNumber = 44;

    /// <summary>Page one, <c>NF_L0_WK100</c> "I wish to shift my class".</summary>
    public const int ShiftClassEntry = 100;

    /// <summary>Item 4266 "Class Shift Permit" (ItemBaseAttribute / EquipName.dat).</summary>
    public const int PermitItemId = 4266;

    /// <summary>Retail limit: "free up to and including level 100".</summary>
    public const int RetailFreeLevel = 100;

    // Index 3 result lines of NpcFunWork.lua.
    public const int ShiftedLine = 130;        // NF_L0_WK116 "Class shifted! ... sealed ... unsealed"
    public const int SameClassLine = 201;      // NF_L0_WK113 "you are a member of that class now"
    public const int EquipmentWornLine = 202;  // NF_L0_WK114 "cannot have anything equipped"
    public const int PermitMissingLine = 203;  // NF_L0_WK115 "above Level 100 should give me 1 Class Shift Permit"

    // REVIEW FIX: a click that finds the character changed underneath it (a GM or editor
    // changed the class while the player was online) gets an answer that closes the dialog
    // instead of none. NF_L0_WK125 "Please make a choice", EndMessage.
    public const int StaleLine = 200;

    /// <summary>
    /// The four page-two buttons and the profession each one names. The button texts are
    /// <c>NF_L0_WK104</c>-<c>WK107</c>: Warrior, Champion, Mage, Priest; the server's
    /// professions are 0 Warrior, 1 Champion, 2 Priest, 3 Mage, so 112 and 113 cross over.
    /// </summary>
    public static bool TryResolveTargetProfession(int click, out byte profession)
    {
        switch (click)
        {
            case 110: profession = 0; return true;
            case 111: profession = 1; return true;
            case 112: profession = 3; return true;
            case 113: profession = 2; return true;
            default: profession = 0; return false;
        }
    }

    /// <summary>
    /// The class button of a 10069 click, or -1. The client keeps the page's own number
    /// (100) in <c>+16</c> and the chosen button in <c>+20</c> (capture 2026-09-28, session
    /// c202c633); a click that carries the button in <c>+16</c> alone is accepted as well.
    /// </summary>
    public static int ResolveClassClick(int selection, int pageSelection)
    {
        if (pageSelection >= 0)
        {
            return selection == ShiftClassEntry && TryResolveTargetProfession(pageSelection, out _)
                ? pageSelection
                : -1;
        }

        return TryResolveTargetProfession(selection, out _) ? selection : -1;
    }

    /// <summary>
    /// Off unless <c>GODSWAR_CLASS_CHANGE=true</c> (or 1) or the hot server rate
    /// <c>npc.class_change</c> is 1. Off, the Class Shifter keeps its old menu-only answers.
    /// </summary>
    public static bool IsEnabled(string? environmentValue, ServerRatesSnapshot rates) =>
        IsTrue(environmentValue) || rates.Absolute(ServerRateKeys.ClassChangeEnabled) == 1;

    public static bool IsEnabled() =>
        IsEnabled(Environment.GetEnvironmentVariable("GODSWAR_CLASS_CHANGE"), ServerRates.Current);

    public static int FreeLevel(ServerRatesSnapshot rates) =>
        (int)rates.Absolute(ServerRateKeys.ClassChangeFreeLevel);

    public static bool RequiresPermit(int level, int freeLevel) => level > freeLevel;

    /// <summary>
    /// Talent points one talent at <paramref name="rank"/> is worth at today's price: what
    /// buying ranks 1..rank costs now (stock price times the <c>cost.talent</c> rate). Used
    /// only when the price actually paid is not on record (see <see cref="TryPaidRefund"/>).
    /// </summary>
    public static long TalentRefund(int rank)
    {
        long total = 0;
        var top = Math.Clamp(rank, 0, TalentProgression.RankCap);
        for (var current = 0; current < top; current++)
        {
            total += TalentProgression.CalculateUpgradeCost(current);
        }

        return total;
    }

    /// <summary>
    /// REVIEW FIX (refund drift): the points actually paid for a talent now at
    /// <paramref name="rank"/>, from the durable talent command receipts (command_inbox,
    /// newest first: rank and cost of each committed purchase). Only accepted when the
    /// newest <paramref name="rank"/> receipts are exactly ranks rank..1 in order and the
    /// newest one is the talent row's current outbox revision, i.e. every rank the player
    /// holds was bought through that path since the last reset. Otherwise false, and the
    /// caller falls back to <see cref="TalentRefund(int)"/> at today's price.
    /// </summary>
    public static bool TryPaidRefund(
        int rank,
        long currentRevision,
        IReadOnlyList<(int Rank, long Cost, long Revision)> newestFirst,
        out long refund)
    {
        refund = 0;
        if (rank <= 0 || newestFirst.Count < rank || newestFirst[0].Revision != currentRevision)
        {
            return false;
        }

        for (var index = 0; index < rank; index++)
        {
            var receipt = newestFirst[index];
            if (receipt.Rank != rank - index || receipt.Cost < 0)
            {
                refund = 0;
                return false;
            }

            refund += receipt.Cost;
        }

        return true;
    }

    /// <summary>Saturates a refund so it can be added to the int talent-point column.</summary>
    public static int SaturateRefund(long total, int pointsBefore) =>
        (int)Math.Clamp(total, 0, (long)int.MaxValue - Math.Max(0, pointsBefore));

    /// <summary>Total refund for the old tree at today's price, saturated.</summary>
    public static int TalentRefund(IEnumerable<int> ranks, int pointsBefore) =>
        SaturateRefund(ranks.Sum(rank => TalentRefund(rank)), pointsBefore);

    private static bool IsTrue(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
}
