using Godswar.Server.Application.EventsNpc;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

// EVENTSNPC: the three event switches default to off, the client reply encodings, the manual routes, the event
// NPC spawns (none while off, no id collision), and the one shared-code change (the quest accept band) is
// unchanged while the Random Quest event is off.
internal static class EventsNpcChecks
{
    public const string CheckName = "Events NPC: Lost Book, Marathon, Random Quest Manager";

    public static Task RunAsync()
    {
        try
        {
            CheckRates();
            CheckEncodings();
            CheckRoutes();
            CheckSpawns();
            CheckAcceptBand();
            CheckReviewFixes();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void Install(params (string Key, decimal Value)[] values) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            values.Select(static value => KeyValuePair.Create(value.Key, value.Value)),
            1, DateTimeOffset.UtcNow, "check", "test"));

    private static void CheckRates()
    {
        ServerRates.ResetToDefaults();
        foreach (var key in new[] { EventsNpcRateKeys.LostBook, EventsNpcRateKeys.Marathon,
                     EventsNpcRateKeys.RandomQuest, EventsNpcRateKeys.MarathonCheckAnswer })
        {
            Check.True(ServerRateCatalog.TryGet(key, out var definition) && definition.DefaultValue == 0m,
                $"{key} is a server rate that defaults to 0 (off)");
        }

        Check.True(!EventsNpcRates.LostBookOn && !EventsNpcRates.MarathonOn && !EventsNpcRates.RandomQuestOn,
            "all three events are off by default");
        Install((EventsNpcRateKeys.Marathon, 1m));
        Check.True(EventsNpcRates.MarathonOn && !EventsNpcRates.LostBookOn, "one switch turns on one event");
    }

    private static void CheckEncodings()
    {
        // NpcFunRemain.lua: (SubID-203)/1000*500 = 2,000 EXP at band 4; "all": floor(SubID/10000)*... per chapter.
        Check.Equal(4203, EventsNpcPolicy.RelicSingleReply(1, 4), "relic 1 single exchange at band 4");
        Check.Equal(5219, EventsNpcPolicy.RelicSingleReply(7, 5), "relic 7 single exchange at band 5");
        Check.Equal(24220, EventsNpcPolicy.RelicAllReply(1, 4, 2), "relic 1 exchange-all, two scrolls, band 4");
        Check.Equal(31221, EventsNpcPolicy.RelicAllReply(6, 1, 3), "relic 6 exchange-all uses the script's 221");
        Check.Equal(1, EventsNpcPolicy.RelicExperienceBand(59), "below 60: 500 EXP");
        Check.Equal(4, EventsNpcPolicy.RelicExperienceBand(120), "100-120: 2,000 EXP");
        Check.Equal(5, EventsNpcPolicy.RelicExperienceBand(121), "121+: 2,500 EXP");
        // NpcFunLostBook.lua: 10 - (SubID-101)/2000 wishes left.
        Check.Equal(6101, EventsNpcPolicy.WishHeader(7), "7 wishes left (5 + 2)");
        Check.Equal(20101, EventsNpcPolicy.WishHeader(0), "no wishes left");
        Check.Equal(17, EventsNpcPolicy.IncompleteWishes.Length, "17 incomplete-book rewards (BK602-711)");
        Check.Equal(11, EventsNpcPolicy.CompleteWishes.Length, "11 complete-book rewards (BK802-905)");
        Check.Equal(602 + 2000 * 80, EventsNpcPolicy.IncompleteWishes[0].Reply(80), "EXP line carries the level");
        Check.Equal(3866, EventsNpcPolicy.Scroll(1, 7), "Athens Sheepskin Scroll 7");
        Check.Equal(3891, EventsNpcPolicy.Scroll(0, 7), "Sparta Sheepskin Scroll 7");
        Check.Equal(180, EventsNpcPolicy.RaceBindingGold(1), "second race of the day pays 180 B-Gold");
        Check.Equal(90, EventsNpcPolicy.RaceBindingGold(2), "third race pays 90 B-Gold");
        var tuesday = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(10));
        Check.True(EventsNpcPolicy.IsRaceOpen(tuesday), "race open Tuesday noon");
        Check.True(!EventsNpcPolicy.IsRaceOpen(tuesday.AddHours(11).AddMinutes(56)), "closed from 23:55");
        Check.True(!EventsNpcPolicy.IsRaceOpen(tuesday.AddDays(5)), "closed on Sunday");
    }

    private static void CheckRoutes()
    {
        foreach (byte camp in new byte[] { 0, 1 })
        {
            Check.Equal(1, EventsNpcPolicy.TrainerFor(camp, 17), $"camp {camp}: Manual 17 goes back to trainer 1");
            Check.True(Enumerable.Range(1, 17).All(n => EventsNpcPolicy.TrainerFor(camp, n) is >= 1 and <= 10),
                $"camp {camp}: every manual names trainer 1-10");
        }

        Check.Equal(1, EventsNpcPolicy.TrainerNumber("Athens_Newbie_024"), "Athens_Newbie_024 is trainer 1");
        Check.Equal(10, EventsNpcPolicy.TrainerNumber("Sparta_082"), "Sparta_082 is trainer 10");
        Check.Equal(0, EventsNpcPolicy.TrainerNumber("Sparta_083"), "the Elder is not a trainer");
        Check.Equal(0, EventsNpcPolicy.TrainerNumber("Athens_Newbie_029"), "a relic is not a trainer");
        Check.Equal(7, EventsNpcPolicy.RelicNumber("Argolis_All_013"), "Argolis_All_013 is relic 7");
        for (var offset = 0; offset < EventsNpcPolicy.ScrollCount; offset++)
        {
            foreach (byte camp in new byte[] { 0, 1 })
            {
                var item = (uint)(EventsNpcPolicy.ScrollItemFirst(camp) + offset);
                Check.True(QuestRuntimeRequirements.ScrollItems.TryGetValue(item, out var quest) &&
                    quest == EventsNpcPolicy.ScrollQuestFirst(camp) + (uint)offset &&
                    EventsNpcPolicy.IsChainQuest(quest), $"Random Quest Scroll {item} opens its chain quest");
            }
        }
    }

    private static void CheckSpawns()
    {
        Check.Equal(0, EventsNpcSpawnPolicy.For(2, [], false, false).Count, "no event NPC while both are off");
        var trainers = EventsNpcSpawnPolicy.For(2, [], false, true);
        var relics = EventsNpcSpawnPolicy.For(2, [], true, false);
        Check.Equal(5, trainers.Count, "map 2 gets Fitness Trainers 1-5 with the Marathon on");
        Check.Equal(2, relics.Count, "map 2 gets Ancient Relics 1 and 6 with the Lost Book on");
        Check.Equal(3, EventsNpcSpawnPolicy.For(1, [], false, true).Count, "Athens gets trainers 8-10");
        var all = new short[] { 0, 1, 2, 3, 4, 5, 11, 13, 16, 18 }
            .SelectMany(map => EventsNpcSpawnPolicy.For(map, [], true, true)).ToList();
        Check.Equal(EventsNpcSpawnPolicy.Count, all.Count, "every event NPC belongs to one known map");
        Check.Equal(all.Count, all.Select(static npc => npc.ObjectId).Distinct().Count(), "event NPC ids are unique");
        Check.True(all.All(static npc => npc.ObjectId is >= 5700 and <= 5733 && npc.InteractionId == npc.ObjectId &&
                npc.X is > -256 and < 256 && npc.Z is > -256 and < 256),
            "event NPC ids are in 5700-5733 and inside the map grid");
        Check.True(all.All(static npc => !CapturedNpcPlacements.TryFind(npc.NpcKey, out var captured) ||
                captured.ObjectId != npc.ObjectId),
            "no event NPC id equals a captured placement id");
        var published = trainers[0] with { NpcKey = "Athens_Newbie_024" };
        Check.Equal(4, EventsNpcSpawnPolicy.For(2, [published], false, true).Count,
            "an NPC already on the map is never doubled");
    }

    private static void CheckAcceptBand()
    {
        var chain = StarterQuestChain.Find(1399u);
        Check.True(chain is { IsQuestScroll: true, MinLevel: 70 }, "quest 1399 is a level-70 scroll quest");
        var main = StarterQuestChain.Steps.First(static step => step.IsMainLine);
        ServerRates.ResetToDefaults();
        var offAt120 = GameClientHandler.IsWithinAcceptBand(120, chain!.Value);
        var offAt75 = GameClientHandler.IsWithinAcceptBand(75, chain.Value);
        var mainOff = GameClientHandler.IsWithinAcceptBand(150, main);
        Check.True(!offAt120 && offAt75, "event off: the nine-level band still shuts a Lv70 scroll quest at 120");
        Install((EventsNpcRateKeys.RandomQuest, 1m));
        Check.True(GameClientHandler.IsWithinAcceptBand(120, chain.Value) &&
            !GameClientHandler.IsWithinAcceptBand(69, chain.Value), "event on: chain quests open from 70 up");
        Check.Equal(mainOff, GameClientHandler.IsWithinAcceptBand(150, main), "main-line band unchanged");
        var other = StarterQuestChain.Steps.First(static step => !step.IsMainLine && !step.IsQuestScroll &&
            !step.IsOneTimeUtility && !step.RequiresExactLevel && step.MinLevel < 100);
        Check.True(!GameClientHandler.IsWithinAcceptBand(other.MinLevel + 20, other),
            "event on: other side quests keep the nine-level band");
    }

    // Review fixes (2026-11-03): one-shot sack state, owed reward, the new columns, and the shared Zeus EXP path
    // still applying exp.events by default (only the pill turn-in opts out).
    private static void CheckReviewFixes()
    {
        var day = new DateOnly(2026, 11, 3);
        var fresh = Godswar.Server.Infrastructure.EventsNpc.EventsNpcState.Fresh(day);
        Check.True(!fresh.SackPending && fresh.RewardStep == 0 && !GameClientHandler.SackOpens(fresh),
            "a fresh state has no sack out, owes nothing, and opens no sack");
        var afterThree = GameClientHandler.AdvanceChain(fresh with { ChainStep = 2, ChainQuestId = 1401 }, 3);
        Check.True(afterThree is { ChainStep: 3, ChainQuestId: 0, SackPending: true, RewardStep: 0 } &&
            GameClientHandler.SackOpens(afterThree), "step 3 paid: a sack is out for step 3 and that one opens");
        var drawn = afterThree with { ChainQuestId = 1402, SackPending = false };
        Check.True(!GameClientHandler.SackOpens(drawn), "after the open a second (stashed) sack opens to nothing");
        Check.True(!GameClientHandler.SackOpens(afterThree with { RewardStep = 4 }), "no sack opens while a reward is owed");
        var done = GameClientHandler.AdvanceChain(fresh with { ChainStep = 9, RewardStep = 10 }, 10);
        Check.True(done is { ChainStep: 0, ChainQuestId: 0, SackPending: false, RewardStep: 0 },
            "the 10th step ends the chain and clears the owed reward");
        var schema = Godswar.Server.Infrastructure.EventsNpc.PostgresEventsNpcStore.SchemaSql;
        Check.True(schema.Contains("ADD COLUMN IF NOT EXISTS sack_pending") &&
            schema.Contains("ADD COLUMN IF NOT EXISTS reward_step"), "first-use DDL adds the two review-fix columns");
        var pay = typeof(GameClientHandler).GetMethod("PayZeusGiftProgressionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var rated = pay?.GetParameters().LastOrDefault();
        Check.True(rated is { Name: "applyEventRate", HasDefaultValue: true } && Equals(rated.DefaultValue, true),
            "the shared Zeus/event EXP path still applies exp.events unless a caller opts out");
    }
}
