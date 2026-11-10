namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// "First Pet" / "[Lv10] First Fuzzy Friend" (IGG guide acid=136: "Any player level 10 or higher can accept the
/// 'First Pet' quest ... from the Newbie Guide ... submit it to the Pet Master to claim a Flower Pixie Egg or a
/// Minotaur Egg"; GodsArena wiki-54 agrees). The stock client still ships the quest as 302 (Sparta) and 1302 (Athens):
/// giver *_094, responder *_110 (Pet Master), TP 2 (Text/Quest/302.dat), but its Quest.xml retired it to
/// MinLevel 200 and the title reads "[Lv200]". The server rows below use the guide's level 10. The egg choice is the
/// quest_reward_slots rows in Development/build/petcapture/migration.sql (slot 0 = 10151, slot 1 = 10152).
/// EXP 0 and TP 2 are the client text's; no other reward is in the guide.
/// Off unless GODSWAR_FIRST_PET_QUEST=1 (read at start-up): whether the client lists a quest whose own Quest.xml
/// says level 200 is UNPROVEN.
/// </summary>
internal static class FirstPetQuest
{
    public const uint SpartaQuestId = 302;
    public const uint AthensQuestId = 1302;

    public static IReadOnlyList<StarterQuestChain.Step> EnabledSteps =>
        Environment.GetEnvironmentVariable("GODSWAR_FIRST_PET_QUEST") == "1" ? Steps : [];

    internal static IReadOnlyList<StarterQuestChain.Step> Steps { get; } =
    [
        new(SpartaQuestId, StarterQuestChain.SpartaCamp, "Sparta_094", "Sparta_110", 0, 2, 0, 0, 10, 200, false,
            StarterQuestChain.SortStory, 0, string.Empty, false, 0, false, 0, 0, IsOneTimeUtility: true),
        new(AthensQuestId, StarterQuestChain.AthensCamp, "Athens_094", "Athens_110", 0, 2, 0, 0, 10, 200, false,
            StarterQuestChain.SortStory, 0, string.Empty, false, 0, false, 1, 1, IsOneTimeUtility: true)
    ];
}
