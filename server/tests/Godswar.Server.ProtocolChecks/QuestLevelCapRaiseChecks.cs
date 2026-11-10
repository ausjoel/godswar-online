using Godswar.Server.Domain.World.Content;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

/// <summary>FINALISE2b (the admin F-121): the 159 former 121-140-capped quests stay open up to Lv200; nothing else changes.</summary>
internal static class QuestLevelCapRaiseChecks
{
    public const string CheckName = "F-121 quest level caps 121-140 raised to 200";

    public static Task RunAsync()
    {
        try
        {
            ServerRates.ResetToDefaults();
            var probe = StarterQuestChain.Steps.First(step => QuestLevelCapRaise.QuestIds.Contains(step.QuestId) &&
                !step.RequiresExactLevel && !step.IsMainLine && !step.IsOneTimeUtility);
            Check.True(!QuestLevelCapRaise.Applies(probe.QuestId) &&
                       !GameClientHandler.IsWithinAcceptBand(probe.MinLevel + 10, probe),
                $"switch quest.caps_121_140_to_200 defaults to 0: quest {probe.QuestId} keeps its nine-level band");
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Finalise2RateKeys.QuestCapsTo200, 1m)], 1, DateTimeOffset.UtcNow, "check", "test"));
            RunOn();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void RunOn()
    {
        Check.Equal(159, QuestLevelCapRaise.QuestIds.Count, "159 quest ids");
        var steps = StarterQuestChain.Steps.Where(step => QuestLevelCapRaise.Applies(step.QuestId)).ToList();
        Check.True(steps.Count > 0, "the raised quests are in the published chain");
        var banded = steps.Where(step => !step.RequiresExactLevel && !step.IsMainLine && !step.IsOneTimeUtility).ToList();
        Check.True(banded.Count > 0, "some raised quests used the nine-level band");
        foreach (var step in banded)
        {
            Check.True(GameClientHandler.IsWithinAcceptBand(200, step), $"quest {step.QuestId} open at 200");
            Check.True(GameClientHandler.IsWithinAcceptBand(step.MinLevel - 5, step), $"quest {step.QuestId} open at MinLevel-5");
            Check.True(!GameClientHandler.IsWithinAcceptBand(step.MinLevel - 6, step), $"quest {step.QuestId} closed below MinLevel-5");
        }

        Check.True(steps.All(step => !step.RequiresExactLevel), "no exact-level daily is in the list");
        var other = StarterQuestChain.Steps.First(step => !QuestLevelCapRaise.Applies(step.QuestId) &&
            !step.RequiresExactLevel && !step.IsMainLine && !step.IsOneTimeUtility && step.MinLevel + 10 <= 200);
        Check.True(!GameClientHandler.IsWithinAcceptBand(other.MinLevel + 10, other),
            $"an unlisted banded quest ({other.QuestId}) keeps its nine-level cap");
    }
}
