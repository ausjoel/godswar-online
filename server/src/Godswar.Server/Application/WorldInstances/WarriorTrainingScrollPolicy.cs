namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// WARRIOR scrolls (the admin F-146 option b, 2026-11-09): the fourteen "Warriors' Training Ground" quest scrolls
/// 4838-4851. Their text promises "kill the Magma Scorpion (魔岩毒蝎) in the Lv60-69 ... Lv120-130 Warriors' Training
/// Ground", but their client Quest field points at unrelated quests (the Parnassus frontline quests 566-571 /
/// 1566-1571 and the Festival Firecracker 572 / 1572), and the client has no Magma Scorpion quest. Option (b): using
/// a scroll arms a hidden server task "kill 1 Magma Scorpion"; a Magma Scorpion killed in any Warriors' Training
/// Ground run completes it for every member in that run who has one armed, and the reward is paid by the 20-second
/// reward loop. No quest window, no pop-up after the item is used (standing rule): info lines only.
/// </summary>
/// <remarks>
/// <para>
/// Client data (ItemBaseAttribute.xml): 4838-4844 are Camp 1 (Athens), 4845-4851 Camp 0 (Sparta); within each camp
/// the seven scrolls are the bands 60-69, 70-79, 80-89, 90-99, 100-109, 110-119 and 120-130. All are PlayLv 60-200.
/// </para>
/// <para>
/// GENERATED: the EXP reward per band (the text only says "rich reward"; scaled from about a quarter of a
/// full ground-2 clear at 60-69 up to 300,000 at 120-130), paid with the live exp.events rate like the ground rewards;
/// "any ground" (the four grounds' levels 40/60/80/91 do not match the seven bands, and the admin's option (b) says "a
/// Warriors' Training Ground"); one armed task per character at a time; party credit (everyone in the run).
/// </para>
/// </remarks>
internal static class WarriorTrainingScrollPolicy
{
    public const int FirstItemId = 4838;
    public const int LastItemId = 4851;
    private const int BandsPerCamp = 7;

    /// <summary>GENERATED EXP per band: 60-69, 70-79, 80-89, 90-99, 100-109, 110-119, 120-130.</summary>
    private static readonly int[] Experience = [50_000, 70_000, 100_000, 140_000, 180_000, 230_000, 300_000];

    public static bool IsScroll(int itemId) => itemId is >= FirstItemId and <= LastItemId;

    public static bool TryResolve(int itemId, out WarriorTrainingScroll scroll)
    {
        scroll = default;
        if (!IsScroll(itemId)) return false;
        var offset = itemId - FirstItemId;
        var band = offset % BandsPerCamp;
        var low = 60 + (band * 10);
        scroll = new(itemId, Camp: offset < BandsPerCamp ? (byte)1 : (byte)0, low, band == 6 ? 130 : low + 9,
            Experience[band]);
        return true;
    }

    /// <summary>The lines sent when a scroll arms the task (each fits one 63-byte note field).</summary>
    public const string ArmedLine = "Task: kill 1 Magma Scorpion in a Warriors' Training Ground.";

    public static string RewardLine(WarriorTrainingScroll scroll) => $"Task reward: {scroll.Experience:N0} EXP (before event bonus).";

    public const string AlreadyArmedLine = "You already have a Magma Scorpion task. Scroll kept.";
    public const string WrongCampLine = "This scroll is for the other faction. Scroll kept.";
    public static string CompletedLine(int experience) => $"Magma Scorpion task done: {experience:N0} base EXP.";
}

/// <summary>WARRIOR scrolls (F-146): one scroll's camp, level band and GENERATED EXP reward.</summary>
internal readonly record struct WarriorTrainingScroll(int ItemId, byte Camp, int BandLow, int BandHigh, int Experience);
