using Godswar.Server.Application.North;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>FINALISE2 P2 (the admin 2026-11-05): world-boss notice text (F-84), Mycenae big bosses (F-19), boss quests.</summary>
internal static class Finalise2NorthChecks
{
    public const string CheckName = "FINALISE2 north: world-boss notice, Mycenae bosses, boss quests";

    public static Task RunAsync()
    {
        // F-84: wiki Elites page factions; location in the notice; native frame length.
        Check.True(WorldBossNoticePolicy.Reaches(18, GameDefaults.AthensCamp) && !WorldBossNoticePolicy.Reaches(18, GameDefaults.SpartaCamp),
            "Megara (18) world boss is announced to Athenians only");
        Check.True(WorldBossNoticePolicy.Reaches(19, GameDefaults.AthensCamp) && !WorldBossNoticePolicy.Reaches(19, GameDefaults.SpartaCamp),
            "Plataea (19) world boss is announced to Athenians only");
        Check.True(WorldBossNoticePolicy.Reaches(16, GameDefaults.SpartaCamp) && !WorldBossNoticePolicy.Reaches(16, GameDefaults.AthensCamp),
            "Argolis (16) world boss is announced to Spartans only");
        Check.True(WorldBossNoticePolicy.Reaches(15, GameDefaults.SpartaCamp) && !WorldBossNoticePolicy.Reaches(15, GameDefaults.AthensCamp),
            "Derveni (15) world boss is announced to Spartans only");
        Check.True(WorldBossNoticePolicy.Reaches(21, GameDefaults.SpartaCamp) && WorldBossNoticePolicy.Reaches(21, GameDefaults.AthensCamp),
            "Olympus (21) world boss is announced to both factions");
        var text = WorldBossNoticePolicy.Text("Lord Barryonyx", "Megara_All", 18, 160.4f, -175.6f);
        Check.Equal("World boss Lord Barryonyx has appeared in Megara at (160, -176)! (Athenian lands)", text, "Megara notice text");
        Check.Equal("World boss Bahamut has appeared in Olympus at (-156, -176)! (open to both factions)",
            WorldBossNoticePolicy.Text("Bahamut", "Olympus_All", 21, -155.8f, -176.2f), "Olympus notice text");
        var longest = WorldBossNoticePolicy.Text("[BOSS]Scorpion Lord Selket", "Isthmus_of_Corinth_All", 17, -209.4f, -147.2f);
        Check.True(longest.Length <= Godswar.Server.Packets.PacketBuilder.CenteredAnnouncementMaximumTextLength,
            "a long notice still fits the native frame");

        // F-19: four bosses, random 30-60 minutes, never the tier window; nothing else changes.
        Check.Equal(4, MycenaeBossPolicy.TemplateKeys.Count, "four Mycenae big bosses");
        for (var i = 0; i < 300; i++)
        {
            Check.True(MycenaeBossPolicy.TryRespawnDelay(6, "A_elite_FireDragonm_001", out var d) &&
                       d >= TimeSpan.FromMinutes(30) && d <= TimeSpan.FromMinutes(60), "Mycenae boss respawn in [30, 60] min");
        }
        Check.True(!MycenaeBossPolicy.TryRespawnDelay(7, "A_elite_FireDragonm_001", out _), "same key on another map is not a Mycenae boss");
        Check.True(!MycenaeBossPolicy.TryRespawnDelay(6, "A_boss_kingofscorpion_001", out _), "Darkmist (world boss) is not one of them");
        Check.True(!MonsterRespawnPolicyRules.TryResolveTierDelay(6, "A_elite_Ahrimanm_001", out _),
            "a Mycenae boss never takes a per-map/tier respawn window");

        // F-19: one daily boss quest per boss and camp at Nestor.
        var steps = NorthLocalQuests.GeneratedSteps.Where(s => MycenaeBossPolicy.QuestIds.Contains(s.QuestId)).ToList();
        Check.Equal(8, steps.Count, "eight Mycenae boss quest steps (4 bosses x 2 camps)");
        Check.True(steps.All(s => s.GiverKey == "Mycenae_All_001" && !s.IsMainLine), "boss quests are Nestor's side dailies");
        Check.Equal(92, NorthLocalQuests.GeneratedSteps.Count, "84 camp quests + 8 boss quests");
        return Task.CompletedTask;
    }
}
