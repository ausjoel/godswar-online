namespace Godswar.Server.Domain.World.Content;

// ROUND-QUESTRESET: editor operations on a character's quest history (issue #18).
// Everything is derived from StarterQuestChain, the pinned quest source the server itself runs.

internal enum QuestChainOperation
{
    /// <summary>Mark the camp's main-line quests up to a level completed. No rewards are granted.</summary>
    CompleteMainLine = 1,

    /// <summary>Forget completed quests, so they can be taken again.</summary>
    ResetCompleted
}

internal enum QuestChainScope
{
    /// <summary>Only the camp's main-line quests, including the daily main line.</summary>
    MainLine = 1,

    /// <summary>Every completed quest.</summary>
    All
}

/// <param name="Changed">Quest ids that become completed (complete) or stop being completed (reset).</param>
/// <param name="CarriedConflicts">
/// Quests in the character's quest log that the change would contradict: a carried quest that would also be
/// completed, or a carried quest whose prerequisite would be forgotten. The caller refuses while any exist.
/// </param>
internal sealed record QuestChainPlan(
    QuestChainOperation Operation,
    IReadOnlyList<uint> Changed,
    IReadOnlyList<uint> CarriedConflicts);

internal static class QuestChainPlanner
{
    public const int MinimumLevel = 1;
    public const int MaximumLevel = 200;

    /// <summary>
    /// The camp's main-line quests whose minimum level is at most <paramref name="level"/>, with every
    /// prerequisite they name. Daily and repeatable rows never count as finished, so they are left out.
    /// This is the same rule the editor's maxer uses (modules/maxer.py main_quests).
    /// </summary>
    public static IReadOnlyList<uint> MainLineThrough(string camp, int level)
    {
        var byId = StarterQuestChain.Steps
            .GroupBy(static step => step.QuestId)
            .ToDictionary(static group => group.Key, static group => group.First());
        var chosen = new SortedSet<uint>();
        foreach (var step in StarterQuestChain.Steps)
        {
            if (IsCountedMainLine(step, camp) && step.MinLevel <= level)
            {
                chosen.Add(step.QuestId);
            }
        }

        var pending = new Stack<uint>(chosen);
        while (pending.Count > 0)
        {
            if (byId.TryGetValue(pending.Pop(), out var step) &&
                step.PrerequisiteQuestId != 0 &&
                byId.TryGetValue(step.PrerequisiteQuestId, out var prerequisite) &&
                IsCountedMainLine(prerequisite, camp) &&
                chosen.Add(prerequisite.QuestId))
            {
                pending.Push(prerequisite.QuestId);
            }
        }

        return chosen.ToArray();
    }

    public static QuestChainPlan Plan(
        QuestChainOperation operation,
        string camp,
        int level,
        QuestChainScope scope,
        IReadOnlyCollection<uint> completed,
        IReadOnlyCollection<uint> carried)
    {
        var completedSet = completed.ToHashSet();
        var carriedSet = carried.ToHashSet();
        if (operation == QuestChainOperation.CompleteMainLine)
        {
            var chain = MainLineThrough(camp, level);
            return new(
                operation,
                chain.Where(id => !completedSet.Contains(id) && !carriedSet.Contains(id)).ToArray(),
                chain.Where(carriedSet.Contains).ToArray());
        }

        var removed = completedSet
            .Where(id => scope == QuestChainScope.All ||
                (StarterQuestChain.Find(id) is { } step && step.IsMainLine && step.Camp == camp))
            .Order()
            .ToArray();
        var removedSet = removed.ToHashSet();
        var conflicts = carriedSet
            .Where(id => StarterQuestChain.Find(id) is { } step &&
                step.PrerequisiteQuestId != 0 &&
                removedSet.Contains(step.PrerequisiteQuestId))
            .Order()
            .ToArray();
        return new(operation, removed, conflicts);
    }

    private static bool IsCountedMainLine(StarterQuestChain.Step step, string camp) =>
        step.IsMainLine && !step.IsDaily && !step.IsRepeat && step.Camp == camp;
}
