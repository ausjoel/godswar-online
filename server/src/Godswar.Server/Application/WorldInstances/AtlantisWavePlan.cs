using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

internal sealed record AtlantisWaveStageDefinition(
    string NormalA, string NormalB, string Elite, string Boss);

internal sealed record AtlantisWaveSpawnSlot(
    int SlotIndex, string DisplayName, AtlantisMonsterRank Rank);

internal sealed record AtlantisWaveDefinition(
    int WaveIndex,
    int StageIndex,
    int GroupIndex,
    ImmutableArray<AtlantisWaveSpawnSlot> Slots)
{
    public bool IsBossWave => GroupIndex == AtlantisWavePlan.GroupsPerStage - 1;
}

/// <summary>
/// User-approved Atlantis roster: four groups of five of each normal monster
/// and two elites, followed by the stage boss. This describes content only;
/// the live adapter owns published templates, coordinates, and monster IDs.
/// </summary>
internal static class AtlantisWavePlan
{
    public const int StageCount = 5;
    public const int GroupsPerStage = 5;
    public const int WaveCount = StageCount * GroupsPerStage;
    public const int TotalMonsterCount = 245;
    public const int TotalPoints = 850;

    public static ImmutableArray<AtlantisWaveStageDefinition> Stages { get; } =
    [
        new("Mudskipper", "Crab Eating Frog", "[Elite]Mudskipper", "Dinna"),
        new("Fiery Turtle", "Deep Sea Spider", "[Elite]Shoal Crocodile", "3-headed Sea Serpent"),
        new("Skeletal Swordsman", "Evil Spirit", "[Elite]Evil Spirit", "Raging Spirit"),
        new("Defensive Witch", "Bewitching Mage", "[Elite]Bewitching Mage", "Prophet"),
        new("Mudskipper", "Evil Spirit", "[Elite]Defensive Witch", "Dinna the Sea Guard")
    ];

    public static ImmutableArray<AtlantisWaveDefinition> Waves { get; } = CreateWaves();

    private static ImmutableArray<AtlantisWaveDefinition> CreateWaves()
    {
        var waves = ImmutableArray.CreateBuilder<AtlantisWaveDefinition>(WaveCount);
        for (var stageIndex = 0; stageIndex < Stages.Length; stageIndex++)
        {
            var stage = Stages[stageIndex];
            for (var groupIndex = 0; groupIndex < GroupsPerStage; groupIndex++)
            {
                var slots = ImmutableArray.CreateBuilder<AtlantisWaveSpawnSlot>();
                if (groupIndex == GroupsPerStage - 1)
                {
                    Append(slots, stage.Boss, AtlantisMonsterRank.Boss, 1);
                }
                else
                {
                    Append(slots, stage.NormalA, AtlantisMonsterRank.Normal, 5);
                    Append(slots, stage.NormalB, AtlantisMonsterRank.Normal, 5);
                    Append(slots, stage.Elite, AtlantisMonsterRank.Elite, 2);
                }
                waves.Add(new(waves.Count, stageIndex, groupIndex, slots.ToImmutable()));
            }
        }
        return waves.MoveToImmutable();
    }

    private static void Append(ImmutableArray<AtlantisWaveSpawnSlot>.Builder slots,
        string name, AtlantisMonsterRank rank, int count)
    {
        for (var index = 0; index < count; index++)
        {
            slots.Add(new(slots.Count, name, rank));
        }
    }
}
