using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class AtlantisWavePlanChecks
{
    public const string CheckName = "Atlantis approved 25-wave roster and 850-point budget";

    public static Task RunAsync()
    {
        var expected = new AtlantisWaveStageDefinition[]
        {
            new AtlantisWaveStageDefinition("Mudskipper", "Crab Eating Frog", "[Elite]Mudskipper", "Dinna"),
            new("Fiery Turtle", "Deep Sea Spider", "[Elite]Shoal Crocodile", "3-headed Sea Serpent"),
            new("Skeletal Swordsman", "Evil Spirit", "[Elite]Evil Spirit", "Raging Spirit"),
            new("Defensive Witch", "Bewitching Mage", "[Elite]Bewitching Mage", "Prophet"),
            new("Mudskipper", "Evil Spirit", "[Elite]Defensive Witch", "Dinna the Sea Guard")
        };
        Check.True(AtlantisWavePlan.Stages.SequenceEqual(expected),
            "Atlantis stages preserve the explicitly approved monster roster");
        Check.Equal(25, AtlantisWavePlan.Waves.Length, "Atlantis has exactly 25 waves");
        Check.True(AtlantisWavePlan.Waves.Select(static wave => wave.WaveIndex)
                .SequenceEqual(Enumerable.Range(0, 25)),
            "every wave has one contiguous immutable index");

        for (var stageIndex = 0; stageIndex < expected.Length; stageIndex++)
        {
            var stage = expected[stageIndex];
            var waves = AtlantisWavePlan.Waves.Where(wave => wave.StageIndex == stageIndex).ToArray();
            Check.True(waves.Select(static wave => wave.GroupIndex).SequenceEqual([0, 1, 2, 3, 4]),
                "each stage contains four clear-triggered groups followed by its boss");
            foreach (var wave in waves.Take(4))
            {
                Check.True(!wave.IsBossWave && wave.Slots.Length == 12 &&
                    wave.Slots.Take(5).All(slot => slot.DisplayName == stage.NormalA &&
                        slot.Rank == AtlantisMonsterRank.Normal) &&
                    wave.Slots.Skip(5).Take(5).All(slot => slot.DisplayName == stage.NormalB &&
                        slot.Rank == AtlantisMonsterRank.Normal) &&
                    wave.Slots.Skip(10).All(slot => slot.DisplayName == stage.Elite &&
                        slot.Rank == AtlantisMonsterRank.Elite) &&
                    wave.Slots.Select(static slot => slot.SlotIndex).SequenceEqual(Enumerable.Range(0, 12)),
                    "a normal group has exactly five A, five B, and two elite ordered slots");
            }
            Check.True(waves[4].IsBossWave && waves[4].Slots.Length == 1 &&
                waves[4].Slots[0] == new AtlantisWaveSpawnSlot(0, stage.Boss, AtlantisMonsterRank.Boss),
                "only the fifth wave contains the approved stage boss");
            var monsters = waves.SelectMany(static wave => wave.Slots).ToArray();
            Check.True(monsters.Count(static slot => slot.Rank == AtlantisMonsterRank.Normal) == 40 &&
                monsters.Count(static slot => slot.Rank == AtlantisMonsterRank.Elite) == 8 &&
                monsters.Count(static slot => slot.Rank == AtlantisMonsterRank.Boss) == 1,
                "each stage totals 40 normal monsters, eight elites, and one boss");
            Check.Equal(170, monsters.Sum(Points), "each stage has exactly 170 available points");
        }

        var all = AtlantisWavePlan.Waves.SelectMany(static wave => wave.Slots).ToArray();
        Check.Equal(245, all.Length, "all waves contain exactly 245 monsters");
        Check.Equal(850, all.Sum(Points), "all waves supply exactly the 850-point completion goal");
        Check.Equal(800, all.Take(all.Length - 1).Sum(Points),
            "Dinna the Sea Guard is the required final 50-point kill after 800 points");
        return Task.CompletedTask;
    }

    private static int Points(AtlantisWaveSpawnSlot slot)
    {
        Check.True(AtlantisEncounterPolicy.TryGetKillPoints(slot.Rank, out var points),
            "every approved wave slot has a supported score rank");
        return points;
    }
}
