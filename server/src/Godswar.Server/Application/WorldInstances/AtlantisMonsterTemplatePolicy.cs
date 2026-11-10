using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Authored Atlantis roster using exact map-205 client template identities.
/// Groups reuse the central room; four groups and one boss form each stage.
/// The stock templates provide rank and appearance, not monster level or HP.
/// </summary>
internal static class AtlantisMonsterTemplatePolicy
{
    public const string SceneKey = "Atlantis_Entrance";
    public const int StageCount = 5;
    public const int GroupMemberCount = 12;
    public const float MinimumBlockedCellClearance = 3.2f;
    public const float MinimumGroupMemberSeparation = 4f;

    // Pinned CTerrain block-table evidence, reproduced in
    // artifacts/atlantis-grouping-20260908/analyze_placement.py.
    // All points belong to entry (171,24)'s four-neighbor component. Their
    // clearance exceeds the Atlantis-specific two-unit idle roam radius.
    // Y=0 is within 0.02 units of the rendered HMP ground at every point.
    public const string TerrainSha256 =
        "C0786BBE3C15AED2216ACF0A5E7665446F79546A7DE88C4417917DBB4F1185A9";
    public const int BlockTableByteOffset = 257_188;
    public const string BlockTableSha256 =
        "C4BDD8B9B0C3CBE1DBD166FB7D620D1C104C5942AF36FDFE196BBD8DF6E14CC5";

    private static readonly ImmutableArray<StageTemplates> Stages =
    [
        new("B_normalG_fish_001", "B_normalG_froggy_001",
            "B_eliteG_fish_001", "B_bossG_octopus_001"),
        new("B_normalG_turtle_006", "B_normalG_spider_003",
            "B_eliteG_crocodilian_003", "B_bossGB_hydra_004"),
        new("B_normalG_skeleton_008", "B_normalGA_wraith_005",
            "B_eliteG_wraith_005", "B_bossG_wraith_002"),
        new("B_normalGB_mage_018", "B_normalGAC_mage_015",
            "B_eliteG_mage_015", "B_bossGC_mage_017"),
        new("B_normalG_fish_001", "B_normalGA_wraith_005",
            "B_eliteG_mage_018", "B_bossGD_octopus_001")
    ];

    private static readonly ImmutableArray<(float X, float Z)> GroupPositions =
    [
        (-78f, 25f), (-74f, 25f), (-70f, 25f),
        (-78f, 29f), (-74f, 29f), (-70f, 29f),
        (-78f, 33f), (-74f, 33f), (-70f, 33f),
        (-78f, 37f), (-74f, 37f), (-70f, 37f)
    ];

    public static AtlantisMonsterSpawnIdentity ResolveGroupMember(
        int stageIndex,
        int slotIndex)
    {
        var stage = GetStage(stageIndex);
        if (slotIndex is < 0 or >= GroupMemberCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }

        var rank = slotIndex < 10
            ? AtlantisMonsterRank.Normal
            : AtlantisMonsterRank.Elite;
        var template = slotIndex < 5
            ? stage.NormalA
            : slotIndex < 10
                ? stage.NormalB
                : stage.Elite;
        var position = GroupPositions[slotIndex];
        return new(template, rank, position.X, 0f, position.Z);
    }

    public static AtlantisMonsterSpawnIdentity ResolveBoss(int stageIndex) =>
        new(GetStage(stageIndex).Boss, AtlantisMonsterRank.Boss,
            -74f, 0f, 31f);

    private static StageTemplates GetStage(int stageIndex)
    {
        if (stageIndex is < 0 or >= StageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(stageIndex));
        }

        return Stages[stageIndex];
    }

    private readonly record struct StageTemplates(
        string NormalA,
        string NormalB,
        string Elite,
        string Boss);
}

internal readonly record struct AtlantisMonsterSpawnIdentity(
    string TemplateKey,
    AtlantisMonsterRank Rank,
    float X,
    float Y,
    float Z);
