using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Quest content the server is willing to run.
/// </summary>
/// <remarks>
/// The stock client renders quest text from its own
/// <c>Localization/&lt;locale&gt;/Text/Quest/&lt;id&gt;.dat</c> and reads the
/// quest definition from <c>Settings/Sys/Quest.xml</c>, so the server only has
/// to agree on the identifier space. This baseline therefore uses the client's
/// own quest ids.
/// <para>
/// Only the first newbie-guide quest is wired up for now: the chain continues
/// 519 -> 520 -> 521 -> 522 at the same NPCs.
/// </para>
/// </remarks>
internal static class QuestContentBaseline
{
    /// <summary>Sparta_094, the Sparta newbie guide.</summary>
    /// <remarks>
    /// Interactions ids come from the runtime <c>npc_spawn_definitions</c> table,
    /// which is authoritative. <c>NpcActorPlacementCatalog</c> in the source
    /// tree is stale for these entries: it lists Sparta_094 as 5093, but the
    /// published spawn data and the reference server both use 5091.
    /// </remarks>
    public const uint SpartaGuideNpcId = 5091;

    /// <summary>Athens_094, the Athens newbie guide.</summary>
    public const uint AthensGuideNpcId = 5233;

    /// <summary>
    /// The quest the reference server handed out from the same guide NPC.
    /// </summary>
    /// <remarks>
    /// The stock client is byte-identical to the one the reference capture was
    /// taken with, and the reference server advertised 5103 through both its
    /// 10083 reply and its 10082 snapshot, so the client accepts this id. It does
    /// not appear in <c>Quest.xml</c> or <c>Text/Quest/&lt;id&gt;.dat</c>, which
    /// means the client resolves this quest from a table that is not in the
    /// localization folder.
    /// </remarks>
    public const uint StarterQuestId = 5103;

    /// <summary>
    /// Scene key the reference server used in its opcode-10083 quest reply.
    /// </summary>
    /// <remarks>
    /// The captured replies carried the constant 0x1AF720 at both +4 and +12
    /// while the client's own request echoed a different scene value, so this is
    /// a server-owned key rather than an echo of the client's request.
    /// </remarks>
    public const uint SceneKey = 0x1AF720;

    /// <summary>
    /// Objective rows published in the login snapshot.
    /// </summary>
    /// <remarks>
    /// Empty, matching the reference server's login frame (its packet 104), which
    /// carried the quest header with no rows. The finished quest state travels in
    /// the opcode-10082 answer instead, whose frame already contains the completed
    /// objective rows and is replayed byte for byte.
    /// </remarks>
    public static IReadOnlyList<PacketBuilder.QuestObjectiveRecord>
        StarterQuestObjectives { get; } = [];

    /// <summary>
    /// Aica, the responder the starter quest is handed in to.
    /// </summary>
    /// <remarks>
    /// Quest 518 in <c>Quest.xml</c> names <c>Sparta_106</c> as its responder, and
    /// the published spawn data gives that NPC interaction id 5103.
    /// </remarks>
    public const uint SpartaGuideResponderNpcId = 5103;

    /// <summary>
    /// Athens_106, the responder the Athens starter quest is handed in to.
    /// </summary>
    /// <remarks>
    /// The camps mirror each other: <c>Sparta_106</c> (5103) receives quest 518
    /// and <c>Athens_106</c> (5245) receives its Athens copy 1518. Both are named
    /// Acacia in the client's own text, and both open with their own script key -
    /// advertising Sparta's key for the Athens npc opened the wrong page.
    /// </remarks>
    public const uint AthensGuideResponderNpcId = 5245;

    /// <summary>
    /// Window flags the reference server used when opening a quest-giver NPC.
    /// Captured on <c>Sparta_094</c>: <c>S2C 10067</c> reported flags 3 with an
    /// empty packed-dialog field, which is what makes the stock client offer the
    /// quest page instead of a description-only window. The responder uses the
    /// same window so the hand-in option is reachable.
    /// </summary>
    public const int QuestOpenFlags = 3;

    /// <summary>
    /// Reward published by the starter quest's own text.
    /// </summary>
    /// <remarks>
    /// <c>Text/Quest/518.dat</c> states "经验：200 / 专长点：2" in both its Append
    /// and EndText blocks, so the hand-in grants 200 experience and 2 talent
    /// points.
    /// </remarks>
    public const int StarterQuestExperienceReward = 200;

    public const int StarterQuestTalentPointReward = 2;

    public static bool IsNewbieGuide(uint interactionId) =>
        interactionId is SpartaGuideNpcId
            or AthensGuideNpcId
            or SpartaGuideResponderNpcId
            or AthensGuideResponderNpcId;

    /// <summary>
    /// True for the NPC either camp's starter quest is handed in to.
    /// </summary>
    /// <remarks>
    /// The reference capture opens the Sparta responder (5103) with its own
    /// 10067 frame (packet 1307, script key <c>Sparta_106</c>) rather than the
    /// guide's. The Athens responder (5245) mirrors it, so the frame is built
    /// from the clicked npc's own key for both camps.
    /// </remarks>
    public static bool IsNewbieGuideResponder(uint interactionId) =>
        interactionId is SpartaGuideResponderNpcId
            or AthensGuideResponderNpcId;

    public static bool TryGetStarterQuest(
        GameCharacter character,
        out uint guideNpcId,
        out uint questId)
    {
        guideNpcId = character.Camp == 0 ? SpartaGuideNpcId : AthensGuideNpcId;
        questId = StarterQuestId;
        return true;
    }
}
