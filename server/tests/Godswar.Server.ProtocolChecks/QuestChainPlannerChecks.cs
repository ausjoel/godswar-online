using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.ProtocolChecks;

// ROUND-QUESTRESET (issue #18): the editor's main-chain completion and quest-history reset.
internal static class QuestChainPlannerChecks
{
    public static Task RunAsync()
    {
        const string sparta = StarterQuestChain.SpartaCamp;
        const string athens = StarterQuestChain.AthensCamp;
        var through38 = QuestChainPlanner.MainLineThrough(sparta, 38);
        var steps = through38.Select(id => StarterQuestChain.Find(id)!.Value).ToArray();
        Check.True(through38.Count > 0 && steps.All(static s => s.Camp == sparta && s.IsMainLine && !s.IsDaily && !s.IsRepeat),
            "main chain through Lv 38 holds only Sparta main-line, non-daily, non-repeat quests");
        Check.True(through38.Contains(518u) && through38.Contains(519u),
            "main chain starts at the first story quest");
        Check.True(steps.All(static s => s.MinLevel <= 38 || s.PrerequisiteQuestId != 0),
            "every quest above the level is there only as a prerequisite");
        Check.True(steps.All(s => s.PrerequisiteQuestId == 0 || through38.Contains(s.PrerequisiteQuestId)),
            "every prerequisite of the chain is in the chain");
        Check.True(!QuestChainPlanner.MainLineThrough(sparta, 200).Any(static id => id is >= 581 and <= 626),
            "the Thebes daily main line is never marked finished");
        Check.True(QuestChainPlanner.MainLineThrough(athens, 38).All(id => StarterQuestChain.Find(id)!.Value.Camp == athens) &&
                   QuestChainPlanner.MainLineThrough(athens, 38).Intersect(through38).Count() == 0,
            "the Athens chain is Athens-only and disjoint from Sparta's");
        Check.True(QuestChainPlanner.MainLineThrough(sparta, 1).Count < through38.Count &&
                   QuestChainPlanner.MainLineThrough(sparta, 200).Count >= through38.Count,
            "a higher level never finishes fewer quests");

        var complete = QuestChainPlanner.Plan(QuestChainOperation.CompleteMainLine, sparta, 38,
            QuestChainScope.MainLine, completed: [518u], carried: [520u]);
        Check.True(!complete.Changed.Contains(518u) && !complete.Changed.Contains(520u) && complete.Changed.Contains(519u),
            "completion skips finished and carried quests");
        Check.True(complete.CarriedConflicts.SequenceEqual([520u]), "a carried chain quest is reported as a conflict");
        var replay = QuestChainPlanner.Plan(QuestChainOperation.CompleteMainLine, sparta, 38,
            QuestChainScope.MainLine, completed: through38, carried: []);
        Check.Equal(0, replay.Changed.Count, "completing twice changes nothing (no reward can repeat)");

        var resetMain = QuestChainPlanner.Plan(QuestChainOperation.ResetCompleted, sparta, 0,
            QuestChainScope.MainLine, completed: [518u, 519u, 100u, 1518u], carried: [520u]);
        Check.True(resetMain.Changed.SequenceEqual([518u, 519u]),
            "reset of the main line forgets only this camp's main-line quests");
        Check.True(resetMain.CarriedConflicts.SequenceEqual([520u]),
            "a carried quest whose prerequisite is forgotten is a conflict");
        var resetAll = QuestChainPlanner.Plan(QuestChainOperation.ResetCompleted, sparta, 0,
            QuestChainScope.All, completed: [100u, 518u], carried: []);
        Check.True(resetAll.Changed.SequenceEqual([100u, 518u]), "reset of everything forgets every completed quest");
        return Task.CompletedTask;
    }
}
