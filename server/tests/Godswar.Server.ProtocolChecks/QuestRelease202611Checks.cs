using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// QUEST-RELEASE-2026-11 (Development/quests-audit/release-2026-11): north quests below
/// the client's 2000 id limit, the 22 archived quests never offered, and the 20-slot npc
/// lists ranked so the cut keeps what the character can use.
/// </summary>
internal static class QuestRelease202611Checks
{
    public const string CheckName = "Quest release 2026-11: north ids, archive, npc list ranking";

    public static Task RunAsync()
    {
        NorthIds();
        StoryChains();
        Archive();
        MarkerRanking();
        HandInRanking();
        return Task.CompletedTask;
    }

    private static void NorthIds()
    {
        var north = NorthQuests.GeneratedSteps;
        Check.Equal(222, north.Count, "north: 111 quests per camp");
        Check.True(north.All(step => step.QuestId is > 0 and < 2000), "north: every id is a client quest id (< 2000)");
        Check.Equal(north.Count, north.Select(step => step.QuestId).Distinct().Count(), "north: ids unique");
        // Works with GODSWAR_NORTH_QUESTS on or off: the chain may hold the north steps once, nothing else at their ids.
        var northIds = north.Select(step => step.QuestId).ToHashSet();
        Check.Equal(NorthQuests.EnabledSteps.Count, StarterQuestChain.Steps.Count(step => northIds.Contains(step.QuestId)),
            "north: no id collides with the published chain");
        Check.Equal(StarterQuestChain.Steps.Count, StarterQuestChain.Steps.Select(step => step.QuestId).Distinct().Count(),
            "north: chain ids unique");
        // NORTH-GIVERS-2026-11: the six givers beside the Altar Envoy (NorthQuestGiverChecks).
        var givers = NorthQuestGivers.All.Select(giver => giver.NpcKey).ToHashSet();
        Check.True(north.All(step => givers.Contains(step.GiverKey) && step.ResponderKey == step.GiverKey),
            "north: all at the north givers");
        Check.Equal(111, north.Count(step => step.Camp == StarterQuestChain.SpartaCamp), "north: Sparta count");
        Check.True(north.Where(step => step.Camp == StarterQuestChain.SpartaCamp).All(step => step.QuestId is >= 1660 and <= 1770),
            "north: Sparta ids 1660-1770");
        Check.True(north.Where(step => step.Camp == StarterQuestChain.AthensCamp).All(step => step.QuestId is >= 1830 and <= 1940),
            "north: Athens ids 1830-1940");
        Check.True(north.All(step => step.MinLevel is >= 141 and <= 200 && step.MaxLevel <= 200), "north: levels 141-200 (V2 bands)");
        var objectives = NorthQuests.AddObjectives(new Dictionary<uint, QuestObjective[]>());
        Check.True(north.All(step => objectives.TryGetValue(step.QuestId, out var o) && o.Length == 1 && o[0].Required > 0),
            "north: one kill objective per quest");
        Check.True(north.Where(step => !step.IsMainLine).All(step => step.IsDaily && step.MaxCompletionsPerDay == 1),
            "north: hunts are dailies");
    }

    private static void StoryChains()
    {
        var north = NorthQuests.GeneratedSteps;
        var story = north.Where(step => step.IsMainLine).ToList();
        Check.Equal(36, story.Count, "story: 3 steps x 6 maps x 2 camps");
        foreach (var group in story.GroupBy(step => (step.Camp, step.Segment)))
        {
            var steps = group.ToList();
            Check.Equal(3, steps.Count, $"story {group.Key}: three steps");
            Check.Equal(0u, steps[0].PrerequisiteQuestId, $"story {group.Key}: step 1 opens alone");
            Check.Equal(steps[0].QuestId, steps[1].PrerequisiteQuestId, $"story {group.Key}: step 2 needs step 1");
            Check.Equal(steps[1].QuestId, steps[2].PrerequisiteQuestId, $"story {group.Key}: step 3 needs step 2");
            Check.True(steps.All(step => !step.IsDaily && step.MaxCompletionsPerDay == 0), $"story {group.Key}: one-time");
        }

        var delphi = story.Where(step => step.Camp == StarterQuestChain.SpartaCamp && step.Segment == "North-Delphi").ToList();
        var hero = new GameCharacter { Id = 9, AccountId = 9, Camp = GameDefaults.SpartaCamp, Level = 141 };
        Check.True(GameClientHandler.IsWithinAcceptBand(141, delphi[0]), "story: Delphi step 1 open at 141");
        Check.True(!GameClientHandler.IsWithinAcceptBand(140, delphi[0]), "story: Delphi step 1 closed at 140");
        Check.True(GameClientHandler.IsWithinAcceptBand(200, delphi[0]), "story: main line never outgrown");
        Check.True(GameClientHandler.IsChainUnlocked(hero, delphi[0]), "story: step 1 unlocked");
        Check.True(!GameClientHandler.IsChainUnlocked(hero, delphi[1]), "story: step 2 locked before step 1");
        hero.QuestCompletedIds = [delphi[0].QuestId];
        Check.True(GameClientHandler.IsChainUnlocked(hero, delphi[1]), "story: step 2 unlocked after step 1");
        Check.True(!GameClientHandler.IsChainUnlocked(hero, delphi[0]), "story: step 1 not offered twice");
        var athensHero = new GameCharacter { Id = 10, AccountId = 10, Camp = GameDefaults.AthensCamp, Level = 141 };
        Check.True(!GameClientHandler.IsChainUnlocked(athensHero, delphi[0]), "story: Sparta step closed to Athens");

        // The north givers together: what a character of each level could take (both camps the same).
        for (var level = 141; level <= 200; level++)
        {
            var open = north.Count(step => step.Camp == StarterQuestChain.SpartaCamp &&
                                           (step.IsMainLine ? step.PrerequisiteQuestId == 0 : true) &&
                                           GameClientHandler.IsWithinAcceptBand(level, step));
            Check.True(open >= 1, $"north: level {level} has an offer at a north giver");
        }
    }

    private static void Archive()
    {
        Check.Equal(22, ArchivedQuests.Ids.Count, "archive: 22 quests");
        var today = QuestDailyState.Today();
        foreach (var questId in ArchivedQuests.Ids)
        {
            var step = StarterQuestChain.Find(questId);
            Check.True(step is not null, $"archive {questId}: still resolves (carried copies can be finished or deleted)");
            var character = new GameCharacter
            {
                Id = 11, AccountId = 11,
                Camp = questId >= 1000 ? GameDefaults.AthensCamp : GameDefaults.SpartaCamp,
                Level = 200, CurrentMap = (byte)Math.Max((short)0, step!.Value.GiverMapId),
            };
            Check.True(!GameClientHandler.CanAcceptQuest(character, step.Value, today), $"archive {questId}: not offered at 200");
            Check.True(!GameClientHandler.CanAcceptQuest(character, step.Value, today, fromScroll: true), $"archive {questId}: not by scroll");
            Check.True(!GameClientHandler.AcceptableQuestIds(character).Contains(questId), $"archive {questId}: not in the open list");
        }
    }

    private static void MarkerRanking()
    {
        // 40 real chain quests, the last ten available: two main-line steps among them.
        var steps = StarterQuestChain.Steps.Where(step => !step.IsMainLine).Take(38).ToList();
        var main = StarterQuestChain.Steps.Where(step => step.IsMainLine).Take(2).ToList();
        var entries = new List<(uint QuestId, uint Available)>();
        for (var index = 0; index < steps.Count; index++)
        {
            entries.Add((steps[index].QuestId, index >= 30 ? 1u : 0u));
        }

        entries.Add((main[0].QuestId, 1u));
        entries.Add((main[1].QuestId, 1u));
        var ranked = GameClientHandler.RankMarkerEntriesForClient(entries);
        Check.Equal(entries.Count, ranked.Count, "rank 10077: nothing dropped before packing");
        Check.True(ranked.Take(2).Select(e => e.QuestId).ToHashSet().SetEquals([main[0].QuestId, main[1].QuestId]),
            "rank 10077: available main-line steps first");
        var level = (uint id) => StarterQuestChain.Find(id)!.Value.MinLevel;
        Check.True(level(ranked[0].QuestId) >= level(ranked[1].QuestId), "rank 10077: higher level first among equals");
        Check.True(ranked.Skip(2).Take(8).All(e => e.Available == 1) && ranked.Skip(10).All(e => e.Available == 0),
            "rank 10077: then the other available ones, then the rest");
        var packet = PacketBuilder.QuestMarkerList(5000u, ranked);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4));
        Check.Equal(PacketBuilder.QuestNpcListCapacity, count, "rank 10077: packed to the client's 20 slots");
        var kept = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12 + (i * 8), 4))).ToHashSet();
        Check.True(entries.Where(e => e.Available == 1).All(e => kept.Contains(e.QuestId)), "rank 10077: every available quest kept");
        var small = entries.Take(5).ToList();
        Check.True(GameClientHandler.RankMarkerEntriesForClient(small).SequenceEqual(small), "rank 10077: short list unchanged");
    }

    private static void HandInRanking()
    {
        var ids = StarterQuestChain.Steps.Take(40).Select(step => step.QuestId).ToList();
        var character = new GameCharacter { Id = 12, AccountId = 12, Camp = GameDefaults.SpartaCamp, Level = 150 };
        character.Quests = [new CharacterQuest { QuestId = ids[39] }, new CharacterQuest { QuestId = ids[25] }];
        var ranked = GameClientHandler.RankHandInEntriesForClient(ids, character);
        Check.Equal(ids[25], ranked[0], "rank 10080: carried first (original order)");
        Check.Equal(ids[39], ranked[1], "rank 10080: carried second");
        var packet = PacketBuilder.QuestHandInMenu(5000u, ranked);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4));
        var kept = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12 + (i * 4), 4))).ToHashSet();
        Check.True(kept.Contains(ids[39]) && kept.Contains(ids[25]), "rank 10080: carried quests inside the 20 slots");
        var small = ids.Take(5).ToList();
        Check.True(GameClientHandler.RankHandInEntriesForClient(small, character).SequenceEqual(small), "rank 10080: short list unchanged");
    }
}
