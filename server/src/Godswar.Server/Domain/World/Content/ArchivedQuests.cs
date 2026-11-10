namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// QUEST-RELEASE-2026-11 (the admin q02, 2026-11-03): quests kept for the record but never offered.
/// </summary>
/// <remarks>
/// IGG switched these 22 off by giving them MinLevel 200 above MaxLevel 120; this server
/// published them at 191-209, so level-200 players were offered Lv2-Lv80 quests
/// (Development/quests-audit/junk-min200-rows.csv). They stay in the generated
/// <see cref="StarterQuestChain"/> so a copy a character already carries still resolves
/// (it can be finished or deleted), but no npc, scroll or lookup offers them again. The
/// database record is <c>quest_archive.archived_quests</c>
/// (Development/quests-audit/release-2026-11/sql/01-archive-22-quests.sql).
/// </remarks>
internal static class ArchivedQuests
{
    public static IReadOnlySet<uint> Ids { get; } = new HashSet<uint>
    {
        151, 152, 183, 184, 185, 283, 284, 285, 286, 301, 650,
        1151, 1152, 1183, 1184, 1185, 1283, 1284, 1285, 1286, 1301, 1650,
    };

    public static bool Contains(uint questId) => Ids.Contains(questId);
}
