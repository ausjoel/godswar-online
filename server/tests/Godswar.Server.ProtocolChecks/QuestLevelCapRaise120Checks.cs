using Godswar.Server.Domain.World.Content;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

/// <summary>ROUND3 F-123 (the admin): the 196 ticked cap-120 main-story quests open to Lv200 behind quest.caps_120_to_200;
/// F-70 adds scroll quests 646/1646 (Lv126-140 -> Lv126-200) under the same switch.</summary>
internal static class QuestLevelCapRaise120Checks
{
    public const string CheckName = "F-123 quest level caps 120 raised to 200";

    public static Task RunAsync()
    {
        try
        {
            ServerRates.ResetToDefaults();
            var steps = QuestLevelCapRaise120.QuestIds
                .Select(id => StarterQuestChain.Find(id))
                .ToList();
            Check.Equal(196, QuestLevelCapRaise120.QuestIds.Count, "196 quest ids");
            Check.True(steps.All(step => step is not null), "every id is a published chain step");
            var raised = steps.Select(step => step!.Value).ToList();
            Check.True(raised.All(step => step.MaxLevel == 120 && step.IsMainLine &&
                                          !ArchivedQuests.Ids.Contains(step.QuestId)),
                "every id is a live main-story step with client MaxLevel 120");
            Check.True(QuestLevelCapRaise120.QuestIds.All(id => !QuestLevelCapRaise.QuestIds.Contains(id)),
                "no overlap with the F-121 (121-140) list");
            // ROUND3 F-70: scroll quests 646/1646 ride the same switch from a small hand-written list.
            Check.True(QuestLevelCapRaiseF70Scroll.QuestIds.SetEquals([646u, 1646u]), "F-70 list is exactly 646 and 1646");
            Check.True(QuestLevelCapRaiseF70Scroll.QuestIds.All(id => !QuestLevelCapRaise120.QuestIds.Contains(id) &&
                                                                      !QuestLevelCapRaise.QuestIds.Contains(id)),
                "F-70 ids overlap neither the 196 F-123 ids nor the F-121 list");
            var scrolls = QuestLevelCapRaiseF70Scroll.QuestIds.Select(id => StarterQuestChain.Find(id)).ToList();
            Check.True(scrolls.All(step => step is not null), "646 and 1646 are published chain steps");
            var scrollSteps = scrolls.Select(step => step!.Value).ToList();
            Check.True(scrollSteps.All(step => step.IsQuestScroll && step.MinLevel == 131 && step.MaxLevel == 120 &&
                                               !step.IsMainLine && !step.RequiresExactLevel && !step.IsOneTimeUtility &&
                                               !ArchivedQuests.Ids.Contains(step.QuestId) &&
                                               !Application.Seasons.OrientalCarnivalSeason.IsScrollQuest(step.QuestId)),
                "646 and 1646 are live Lv131 scroll quests on the nine-level band");
            var scrollRewardsBefore = scrollSteps.Select(Rewards).ToList();
            var exact = raised.Where(step => step.RequiresExactLevel).ToList();
            var open = raised.Where(step => !step.RequiresExactLevel).ToList();
            Check.Equal(92, exact.Count, "92 exact-level Thebes dailies");
            Check.True(exact.All(step => step.IsDaily), "the exact-level ones stay one-a-day dailies");
            var rewardsBefore = raised.Select(Rewards).ToList();

            // Switch off (default): nothing changes.
            Check.True(QuestLevelCapRaise120.QuestIds.All(id => !QuestLevelCapRaise120.Applies(id)),
                "switch quest.caps_120_to_200 defaults to 0");
            foreach (var step in exact)
            {
                Check.True(GameClientHandler.IsWithinAcceptBand(step.MinLevel, step) &&
                           !GameClientHandler.IsWithinAcceptBand(step.MinLevel + 1, step) &&
                           !GameClientHandler.IsWithinAcceptBand(150, step),
                    $"switch 0: quest {step.QuestId} is exactly Lv{step.MinLevel}");
            }

            Check.True(QuestLevelCapRaiseF70Scroll.QuestIds.All(id => !QuestLevelCapRaiseF70Scroll.Applies(id)),
                "switch 0: F-70 not applied");
            foreach (var step in scrollSteps)
            {
                Check.True(!GameClientHandler.IsWithinAcceptBand(125, step) &&
                           GameClientHandler.IsWithinAcceptBand(126, step) &&
                           GameClientHandler.IsWithinAcceptBand(140, step) &&
                           !GameClientHandler.IsWithinAcceptBand(141, step) &&
                           !GameClientHandler.IsWithinAcceptBand(200, step),
                    $"switch 0: scroll quest {step.QuestId} stays Lv126-140");
            }

            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Finalise2RateKeys.QuestCaps120To200, 1m)], 1, DateTimeOffset.UtcNow, "check", "test"));

            Check.True(QuestLevelCapRaise120.QuestIds.All(QuestLevelCapRaise120.Applies), "switch 1 applies to all 196");
            foreach (var step in exact)
            {
                Check.True(GameClientHandler.IsWithinAcceptBand(step.MinLevel, step) &&
                           GameClientHandler.IsWithinAcceptBand(150, step) &&
                           GameClientHandler.IsWithinAcceptBand(200, step) &&
                           !GameClientHandler.IsWithinAcceptBand(201, step) &&
                           !GameClientHandler.IsWithinAcceptBand(step.MinLevel - 1, step),
                    $"switch 1: quest {step.QuestId} open Lv{step.MinLevel}-200, closed below");
            }

            foreach (var step in open)
            {
                Check.True(GameClientHandler.IsWithinAcceptBand(200, step) &&
                           (step.MinLevel <= 1 || !GameClientHandler.IsWithinAcceptBand(step.MinLevel - 1, step)),
                    $"switch 1: main-story quest {step.QuestId} open from Lv{step.MinLevel} to 200");
            }

            Check.True(QuestLevelCapRaiseF70Scroll.QuestIds.All(QuestLevelCapRaiseF70Scroll.Applies), "switch 1 applies to 646/1646");
            foreach (var step in scrollSteps)
            {
                Check.True(!GameClientHandler.IsWithinAcceptBand(125, step) &&
                           GameClientHandler.IsWithinAcceptBand(126, step) &&
                           GameClientHandler.IsWithinAcceptBand(141, step) &&
                           GameClientHandler.IsWithinAcceptBand(150, step) &&
                           GameClientHandler.IsWithinAcceptBand(200, step) &&
                           !GameClientHandler.IsWithinAcceptBand(201, step),
                    $"switch 1: scroll quest {step.QuestId} open Lv126-200, closed at 125 and 201");
            }

            Check.True(scrollSteps.Select(step => Rewards(StarterQuestChain.Find(step.QuestId)!.Value)).SequenceEqual(scrollRewardsBefore),
                "F-70: 646/1646 rewards stay as authored");

            var unlistedExact = StarterQuestChain.Steps
                .Where(step => step.RequiresExactLevel && !QuestLevelCapRaise120.QuestIds.Contains(step.QuestId))
                .ToList();
            Check.True(unlistedExact.All(step => !GameClientHandler.IsWithinAcceptBand(step.MinLevel + 1, step)),
                $"switch 1: the {unlistedExact.Count} unlisted exact-level quests stay exact");
            var unlistedBanded = StarterQuestChain.Steps.First(step =>
                !QuestLevelCapRaise120.QuestIds.Contains(step.QuestId) && !QuestLevelCapRaise.QuestIds.Contains(step.QuestId) &&
                !QuestLevelCapRaiseF70Scroll.QuestIds.Contains(step.QuestId) &&
                step.IsNineLevelLimited && step.MaxLevel == 120 && step.MinLevel + 10 <= 200 &&
                !Application.Seasons.OrientalCarnivalSeason.IsScrollQuest(step.QuestId));
            Check.True(!GameClientHandler.IsWithinAcceptBand(unlistedBanded.MinLevel + 10, unlistedBanded),
                $"switch 1: an unticked cap-120 quest ({unlistedBanded.QuestId}) keeps its nine-level band");
            Check.True(raised.Select(step => Rewards(StarterQuestChain.Find(step.QuestId)!.Value)).SequenceEqual(rewardsBefore),
                "rewards (EXP, TP, silver, gold) stay as authored");
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static (int, int, int, int) Rewards(StarterQuestChain.Step step) =>
        (step.Experience, step.TalentPoints, step.Silver, step.Gold);
}
