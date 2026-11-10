using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// KELSIS (F-15, 2026-11-09): Delighted Kelsis' "lowest stat" dog contest on Lelantine Farm
/// (<c>Lelantine_Farm_002</c>, object 5619). Every number here is read from the client's own
/// <c>NpcFunFarm.lua</c> (function 47, <c>NPC_FLAG_SYS_FARM</c>) and <c>LuaText.lua</c>
/// (<c>NF_L0_FRAM105/205/206</c>, <c>10000-10008</c>, <c>20000/20001</c>, <c>407/408</c>, <c>410-412</c>,
/// <c>421/422</c>, <c>430/440</c>, <c>201/261/271</c>) and the Awarder's <c>NpcFunWar.lua</c> (<c>FRAM373</c>,
/// <c>207/208/219</c>). No Kelsis session was ever captured; the page counter (one page per 10070 the client
/// receives) is capture-settled on the Advance Troop Captain, the same window and function
/// (<c>{101..104}</c> at Index 1, <c>{31..34}</c> at Index 2, <c>{60}</c> at Index 3). Values marked GENERATED
/// are this server's choices.
/// </summary>
internal static class KelsisContestPolicy
{
    public const string NpcKey = "Lelantine_Farm_002";

    /// <summary>The farm window (<c>NpcFunFarm.lua</c>), as the Advance Troop Captain's capture advertises it.</summary>
    public const int FunctionNumber = LelantineFarmProtocol.FarmDialogIndex;

    // Index 1 (the page request's reply): FRAM105 text + "*What do you want?" (5), "*I would like to give you a dog" (6).
    public const int AskEntry = LelantineFarmProtocol.FarmPetRequest;
    public const int GiveEntry = LelantineFarmProtocol.FarmPetSubmit;

    public static readonly int[] OpeningMenu = [AskEntry, GiveEntry];

    // Index 2.
    /// <summary>10001-10007: "I want a dog with the lowest &lt;stat&gt;..." (ends the window).</summary>
    public const int RequestBase = 10000;

    /// <summary>20001-20007: "Pet type: Dog", "Required Stat: &lt;stat&gt; Lowest" and the FRAM408 rules.</summary>
    public const int SubmitPageBase = 20000;

    public const int NobodySubmitted = 17;   // NF_L0_FRAM410 "No one has submitted a dog!"
    public const int YoursIsBest = 18;       // NF_L0_FRAM411 "By far, the dog you submitted is the best."
    public const int YoursIsNotBest = 19;    // NF_L0_FRAM412 "By far, the dog you submitted is not the best."
    public const int SubmitButton = LelantineFarmProtocol.FarmPetSubmitPage; // 21, NF_L0_FRAM421 "*Submit a dog"

    // Index 3 (all end the window).
    public const int Submitted = 300;        // NF_L0_FRAM430 "Pet submitted!"
    public const int NotEligible = 400;      // NF_L0_FRAM440 "The pet does not meet the requirements."
    public const int BagFull = 500;          // NF_L0_FRAM201 "Your inventory is full, so I cannot give you anything."
    public const int NotSummoned = 600;      // NF_L0_FRAM261 "You have not yet summoned the pet."
    public const int NotADog = 700;          // NF_L0_FRAM271 "I only want a dog now."

    // Battlefield Awarder (NpcFunWar.lua, function 3) page 3 entry 73 "*Special Reward: Kelsis' Pet".
    public const int AwarderWon = 208;       // NF_L0_FRAM809 "...She has rewarded you with 2300 B-Gold."
    public const int AwarderLost = 219;      // NF_L0_FRAM808 "Sorry, the pet you submitted is not Kelsis' final choice."

    /// <summary>The prize every client line names (FRAM105/464/809, SM_51060, LD_10003).</summary>
    public const int PrizeBindingGold = 2300;

    /// <summary>The Loyal Dog, the only "Dog" pet type (<c>PetSpeciesCatalog</c> species 10).</summary>
    public const short DogSpecies = 10;

    /// <summary>
    /// The Morning Dew handed over for the dog (client <c>SM_51180</c> "Kelsis exchanges 1 Morning Dew with ... for a
    /// dog" and reply 500 "inventory full"). Which dew is GENERATED: Morning Dew 2 (Restricted), the one the IGG
    /// guide names and the Mystery Merchant sells.
    /// </summary>
    public const int MorningDewItem = 10141;

    /// <summary>Stat index 1..7 in the client's order: Rank, Agility, Strength, Accuracy, Technique, Wisdom, Luck.</summary>
    public static readonly string[] StatNames =
        ["Rank", "Agility", "Strength", "Accuracy", "Technique", "Wisdom", "Luck"];

    /// <summary>
    /// The stat Kelsis wants this round, 1..7. GENERATED: picked from a hash of the round's start, so every
    /// server process agrees without a stored row and the next round normally asks for another stat.
    /// </summary>
    public static int StatForRound(DateTimeOffset roundStart)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            "kelsis:" + roundStart.ToUniversalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture)));
        return 1 + (hash[0] % StatNames.Length);
    }

    /// <summary>
    /// The pet column the stat is read from: 0 = <c>character_pets.rank</c>; 1..6 = <c>stat_code</c> of
    /// <c>character_pet_stat_values</c> (the same 0..6 order the Rank/Agility.../Luck Spells use).
    /// </summary>
    public static short StatCode(int stat) =>
        stat is >= 1 and <= 7 ? checked((short)(stat - 1)) : throw new ArgumentOutOfRangeException(nameof(stat));

    /// <summary>
    /// The "Pet stat:" line. The script draws <c>(SubID - 9) / 10000</c> with <c>%.2f</c> when
    /// <c>SubID % 100 == 9</c>, so a value v is sent as <c>round(v * 100) * 100 + 9</c>.
    /// </summary>
    public static int EncodeStatValue(decimal value)
    {
        var hundredths = decimal.Round(Math.Max(0m, value) * 100m, 0, MidpointRounding.AwayFromZero);
        return checked((int)Math.Min(hundredths, (int.MaxValue - 9) / 100) * 100 + 9);
    }

    public static decimal DecodeStatValue(int subId) => (subId - 9) / 10000m;

    /// <summary>
    /// the admin F-15 "lowest-only": a dog is taken only when its stat is strictly lower than the best (lowest) one
    /// already submitted this round (client FRAM408 rule 1). A tie keeps the earlier dog (GENERATED).
    /// </summary>
    public static bool Accepts(decimal value, decimal? best) => best is not { } current || value < current;

    /// <summary>The reply to "*What do you want?" (Index 2).</summary>
    public static int[] RequestReply(int stat) => [RequestBase + stat];

    /// <summary>
    /// The reply to "*I would like to give you a dog" (Index 2): the stat line, then the standing line, then the
    /// "Pet stat:" line when there is a value to show, then the submit button.
    /// </summary>
    /// <param name="stat">This round's stat, 1..7.</param>
    /// <param name="anySubmission">Whether anyone has a dog in this round.</param>
    /// <param name="mineIsBest">null = this player has no dog in the round; else whether theirs is the best.</param>
    /// <param name="shownValue">
    /// The value drawn on "Pet stat:" (GENERATED choice): the player's own submitted dog, otherwise the summoned
    /// dog they could submit; null draws no value line.
    /// </param>
    public static int[] SubmitPageReply(int stat, bool anySubmission, bool? mineIsBest, decimal? shownValue)
    {
        var reply = new List<int>(4) { SubmitPageBase + stat };
        if (mineIsBest is { } best)
        {
            reply.Add(best ? YoursIsBest : YoursIsNotBest);
        }
        else if (!anySubmission)
        {
            reply.Add(NobodySubmitted);
        }

        if (shownValue is { } value)
        {
            reply.Add(EncodeStatValue(value));
        }

        reply.Add(SubmitButton);
        return [.. reply];
    }

    /// <summary>The click a request names, from the path words at +12 onward (DialogPath).</summary>
    public static KelsisClick Classify(IReadOnlyList<int> path) => path switch
    {
        [] => KelsisClick.PageRequest,
        [AskEntry] => KelsisClick.Ask,
        [GiveEntry] => KelsisClick.Give,
        [GiveEntry, SubmitButton] => KelsisClick.Submit,
        _ => KelsisClick.Unknown
    };
}

internal enum KelsisClick
{
    Unknown,
    PageRequest,
    Ask,
    Give,
    Submit
}
