using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// WARRIOR (the admin F-51, 2026-11-09): the Warriors' Training Ground (勇士试炼场),
/// Instance Caller root 13. Four grounds on the client's own Field_Test maps,
/// run by the shared Training Ground runtime (GameSessionRegistry.TrainingGround).
/// </summary>
/// <remarks>
/// <para>
/// Source (GodsArena wiki "Training Ground", Reference/godsarena-wiki/export/book.md):
/// solo or a party of up to 5; each of the four grounds once a day; a run lasts up
/// to 20 minutes, then everyone goes back to the main city; killed players revive
/// near the entrance and carry on; 200 points clear the ground (regular monster 1,
/// elite 5, boss 10); Ground 1 level 40+ (boss Nemean Lion, monsters 40-50),
/// Ground 2 level 60+ (Ceryneian Hind, 60-70), Ground 3 level 80+ (Cretan Bull,
/// 80-90), Ground 4 level 91+ (Lernaean Hydra, 91-120). EXP and skill points by
/// score and clear time are the wiki's two reward tables, copied exactly.
/// </para>
/// <para>
/// Client: NpcFunRepetition.lua draws root 13's page as text 203 and the buttons
/// 200 / 201 / 202 / 301 ("Training Ground 1 (Level 40)", "(Level 60)",
/// "Training Ground 2 (Level 80)", "Training Ground 3 (Level 91)"). The maps are
/// Field_Test (201, scene 219), Field_Test2 (202, 220), Field_Test3 (203, 221) and
/// Field_Test4 (206, 225); their own Monster.ini files carry exactly the wiki's
/// four bosses, so no client file changes.
/// </para>
/// <para>
/// GENERATED (not in either source): the arrival point (176, 3) on the shared
/// Field_Test block plane; the level caps (200 = the game's cap); the daily-entry
/// kinds 21-24; and the monster layout, levels and HP
/// (Development/training-ground/gen_warrior_spawns.py).
/// </para>
/// </remarks>
internal static class WarriorTrainingGroundPolicy
{
    public const int NormalPoints = 1;
    public const int ElitePoints = 5;
    public const int BossPoints = 10;
    public const int TargetScore = 200;

    /// <summary>GENERATED arrival / revive point, the same on all four maps (their block planes are identical).</summary>
    public const float ArrivalX = 176f;
    public const float ArrivalZ = 3f;

    /// <summary>Wiki: a run lasts up to 20 minutes. TEST-ONLY: the rig may shorten it with
    /// GODSWAR_RIG_WARRIOR_TRAINING_TIME_LIMIT_SECONDS (60-1200); unset it is always 20 minutes.</summary>
    public static readonly TimeSpan TimeLimit = ResolveTimeLimit(
        Environment.GetEnvironmentVariable("GODSWAR_RIG_WARRIOR_TRAINING_TIME_LIMIT_SECONDS"));

    internal static TimeSpan ResolveTimeLimit(string? rigSeconds) =>
        int.TryParse(rigSeconds, out var seconds) && seconds is >= 60 and <= 1200
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(20);

    /// <summary>Wiki: the best tier is "200 within 15 minutes".</summary>
    public static readonly TimeSpan FastClear = TimeSpan.FromMinutes(15);

    /// <summary>Elites and bosses appear once per run (GENERATED); normals keep the
    /// ordinary respawn so 200 points can be reached (the layout holds 132 once).</summary>
    public static readonly TimeSpan OncePerRunRespawnInterval = TimeSpan.FromHours(1);

    public const int MaximumLevel = 200; // GENERATED: the wiki gives floors only

    /// <summary>
    /// GENERATED spawn object ids (warrior-spawns.sql): ground g uses
    /// 49001 + 200 x (g - 1) up to 84 ids. Kept out of the world runtime-spawn pool
    /// 48000-48999 (WorldRuntimeSpawnOverlay), Troy (45101-45524, NPCs 47930-47938),
    /// Heracles (46001-46075) and pet capture (49901-49912), and at most 49999
    /// (the monster id registry).
    /// </summary>
    public const uint FirstObjectId = 49_001;
    public const uint LastObjectId = 49_684;

    public static (uint First, uint Last) ObjectIdRange(int ground) =>
        (FirstObjectId + 200u * (uint)(ground - 1), FirstObjectId + 200u * (uint)(ground - 1) + 83u);

    public static IReadOnlyList<WarriorTrainingGround> Definitions { get; } =
    [
        new(1, 200, 201, 219, "Field_Test", 40, "A_bossE_lion_002",
            ["A_bossE_scorpion_008", "A_bossE_lion_002"], AEliteKeys),
        new(2, 201, 202, 220, "Field_Test2", 60, "A_bossE_dryad_001_000",
            ["A_bossE_scorpion_008", "A_bossE_dryad_001_000"], AEliteKeys),
        new(3, 202, 203, 221, "Field_Test3", 80, "A_bossE_bull_001",
            ["A_bossE_scorpion_008", "A_bossE_bull_001"], AEliteKeys),
        new(4, 301, 206, 225, "Field_Test4", 91, "B_bossA_dracoladon_003",
            ["B_bossE_scorpion_008", "B_bossA_dracoladon_003"],
            ["A_eliteEA_dragon_004", "A_eliteEA_snake_008", "B_eliteE_boar_001", "B_eliteE_dryad_001",
             "B_eliteE_fairy_003", "B_eliteE_froggy_003", "B_eliteE_harpies_003", "B_eliteE_wasp_001"])
    ];

    private static string[] AEliteKeys =>
    [
        "A_eliteE_boar_001", "A_eliteE_dragon_004", "A_eliteE_dryad_001", "A_eliteE_fairy_003",
        "A_eliteE_froggy_003", "A_eliteE_harpies_003", "A_eliteE_snake_008", "A_eliteE_wasp_001"
    ];

    public static IReadOnlyList<TrainingGroundDefinition> Grounds { get; } =
        [.. Definitions.Select(static ground => ground.ToDefinition())];

    public static bool IsWarriorKind(InstanceCallerEntryKind kind) =>
        kind is InstanceCallerEntryKind.WarriorTrainingGround1 or
            InstanceCallerEntryKind.WarriorTrainingGround2 or
            InstanceCallerEntryKind.WarriorTrainingGround3 or
            InstanceCallerEntryKind.WarriorTrainingGround4;

    public static bool TryGetByPage(int pageSubId, out WarriorTrainingGround ground)
    {
        ground = Definitions.FirstOrDefault(candidate => candidate.PageSubId == pageSubId)!;
        return ground is not null;
    }

    public static bool TryGetByMap(int mapId, out WarriorTrainingGround ground)
    {
        ground = Definitions.FirstOrDefault(candidate => candidate.MapId == mapId)!;
        return ground is not null;
    }

    public static bool TryGetByKind(InstanceCallerEntryKind kind, out WarriorTrainingGround ground)
    {
        ground = Definitions.FirstOrDefault(candidate => candidate.EntryKind == kind)!;
        return ground is not null;
    }

    public static bool TryGetByScene(int clientSceneId, out WarriorTrainingGround ground)
    {
        ground = Definitions.FirstOrDefault(candidate => candidate.ClientSceneId == clientSceneId)!;
        return ground is not null;
    }

    /// <summary>
    /// Wiki: regular 1, elite 5, boss 10, by the client key's rank (A_/B_ +
    /// normal / elite / boss). Only a ground's own templates are allowed on its
    /// map, so the rank prefix is the whole rule; anything else scores nothing.
    /// </summary>
    public static int ScoreFor(string templateKey)
    {
        if (templateKey.Length < 3 || templateKey[1] != '_' || templateKey[0] is not ('A' or 'B'))
        {
            return 0;
        }
        var rank = templateKey.AsSpan(2);
        return rank.StartsWith("boss", StringComparison.Ordinal) ? BossPoints
            : rank.StartsWith("elite", StringComparison.Ordinal) ? ElitePoints
            : rank.StartsWith("normal", StringComparison.Ordinal) ? NormalPoints
            : 0;
    }

    /// <summary>
    /// The Magma Scorpion (zh 魔岩毒蝎, English client name "Cliff Scorpion"), the
    /// target of the "Warriors' Training Ground" quest scrolls (items 4838-4851).
    /// </summary>
    public static bool IsMagmaScorpion(string templateKey) =>
        templateKey is "A_bossE_scorpion_008" or "B_bossE_scorpion_008";

    /// <summary>
    /// The wiki's reward tables, exactly: grounds 1-3 share the thresholds and
    /// skill points and differ in EXP; ground 4 has its own table. A clear within
    /// 15 minutes pays the top tier, within 20 the next.
    /// </summary>
    public static TrainingGroundReward RewardFor(int ground, int score, TimeSpan? clearedAfter)
    {
        if (ground is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(ground));
        }
        var column = ground - 1;
        // Rows: incomplete 0 / 80 / 120 / 160, complete within 20 min, within 15 min.
        ReadOnlySpan<int> experience1 = [10_000, 50_000, 70_000, 80_000, 90_000, 110_000];
        ReadOnlySpan<int> experience2 = [30_000, 108_000, 144_000, 192_000, 216_000, 228_000];
        ReadOnlySpan<int> experience3 = [60_000, 240_000, 288_000, 312_000, 360_000, 384_000];
        ReadOnlySpan<int> experience4 = [65_000, 400_000, 480_000, 560_000, 640_000, 720_000];
        ReadOnlySpan<int> talent123 = [2, 3, 6, 9, 12, 16];
        ReadOnlySpan<int> talent4 = [3, 8, 12, 18, 22, 30];
        string[] tiers = ["incomplete-0", "incomplete-80", "incomplete-120", "incomplete-160", "completed", "completed-fast"];

        var row = score >= TargetScore && clearedAfter is { } elapsed
            ? elapsed <= FastClear ? 5 : 4
            : score >= 160 ? 3
            : score >= 120 ? 2
            : score >= 80 ? 1
            : 0;
        var exp = column switch
        {
            0 => experience1[row],
            1 => experience2[row],
            2 => experience3[row],
            _ => experience4[row]
        };
        var tp = column == 3 ? talent4[row] : talent123[row];
        return new(exp, tp, tiers[row]);
    }
}

/// <summary>WARRIOR (F-51): one Warriors' Training Ground (page button, map, scene, level floor, bosses, elites).</summary>
internal sealed record WarriorTrainingGround(
    int Ground,
    int PageSubId,
    byte MapId,
    int ClientSceneId,
    string SceneKey,
    int MinimumLevel,
    string GroundBossKey,
    string[] BossKeys,
    string[] EliteKeys)
{
    public InstanceCallerEntryKind EntryKind => Ground switch
    {
        1 => InstanceCallerEntryKind.WarriorTrainingGround1,
        2 => InstanceCallerEntryKind.WarriorTrainingGround2,
        3 => InstanceCallerEntryKind.WarriorTrainingGround3,
        _ => InstanceCallerEntryKind.WarriorTrainingGround4
    };

    /// <summary>The editor's level-band key (public.level_band_overrides), e.g. instance.warrior_training_1.</summary>
    public string GateKey => $"instance.warrior_training_{Ground}";

    public string DisplayName => $"Warriors' Training Ground {Ground}";

    public TrainingGroundDefinition ToDefinition() => new(
        $"warrior{Ground}",
        DisplayName,
        MapId,
        ClientSceneId,
        WarriorTrainingGroundPolicy.ArrivalX,
        WarriorTrainingGroundPolicy.ArrivalZ,
        WarriorTrainingGroundPolicy.TimeLimit,
        WarriorTrainingGroundPolicy.FastClear,
        WarriorTrainingGroundPolicy.TargetScore,
        EntryKind)
    {
        Score = WarriorTrainingGroundPolicy.ScoreFor,
        Reward = (score, clearedAfter) => WarriorTrainingGroundPolicy.RewardFor(Ground, score, clearedAfter),
        IsEliteKill = static key => WarriorTrainingGroundPolicy.ScoreFor(key) >= WarriorTrainingGroundPolicy.ElitePoints
    };

    public InstanceCallerEntryDestination Destination => new(
        EntryKind,
        $"WarriorTraining{Ground}",
        TargetMapId: MapId,
        TargetX: WarriorTrainingGroundPolicy.ArrivalX,
        TargetZ: WarriorTrainingGroundPolicy.ArrivalZ,
        MinimumLevel: MinimumLevel,
        MaximumLevel: WarriorTrainingGroundPolicy.MaximumLevel,
        RequiredPartySize: null)
    {
        ClientSceneId = ClientSceneId,
        MinimumPartySize = 1
    };
}
