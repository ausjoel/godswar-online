using System.Collections.Frozen;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// TRAINING: Training Ground (Trials of Heracles) run rules for map 210.
/// </summary>
/// <remarks>
/// <para>
/// the admin (DECISIONS-2026-11-03, round 2 item 6): map 210 populated with assorted
/// spider / scorpion cave-type monsters of level 75-90, normals 5x and elites
/// 10x the HP of a regular monster of that level, 8-10 elites scattered, 300
/// points to clear (elite 10, normal 1), 30 minutes. "Roughly the mechanic of the
/// old training grounds".
/// </para>
/// <para>
/// Retail (IGG guide acid=166): one entry a day; revive near the entrance; when
/// the time is up the team goes back to the main city; leaving during the
/// mission forfeits the points; a leader termination vote pays "based on the
/// level of completion". EXP and TP (talent points) by completion: the guide's
/// Training Ground 2 column (monsters level 80-90, the band nearest the admin's):
/// 60,000 / 240,000 / 288,000 / 312,000 / 360,000 / 384,000 EXP and 2 / 3 / 6 / 9
/// / 12 / 16 TP.
/// </para>
/// <para>
/// GENERATED (not retail): the guide's thresholds (0 / 80 / 120 / 160 / 200
/// points, "within 20 minutes" / "within 15 minutes") are scaled to the admin's 300
/// points and 30 minutes by the same ratios: 0 / 120 / 180 / 240 / 300 points,
/// within 30 minutes / within 22 min 30 s. The monster templates, positions,
/// levels and HP are generated content (Development/build/training/content).
/// </para>
/// </remarks>
internal static class TrainingGroundPolicy
{
    public const byte MapId = InstanceCallerProtocol.TrainingGroundMapId;

    public const int ClientSceneId = InstanceCallerProtocol.TrainingGroundClientSceneId;

    public const float ArrivalX = InstanceCallerProtocol.HeraclesTrialArrivalX;

    public const float ArrivalZ = InstanceCallerProtocol.HeraclesTrialArrivalZ;

    /// <summary>the admin: 30 minutes (the IGG ground ran 20). TEST-ONLY: the rig may
    /// shorten it with GODSWAR_RIG_TRAINING_TIME_LIMIT_SECONDS (60-1800) to run the
    /// timeout path; unset (live) it is always 30 minutes.</summary>
    public static readonly TimeSpan TimeLimit = ResolveTimeLimit(
        Environment.GetEnvironmentVariable("GODSWAR_RIG_TRAINING_TIME_LIMIT_SECONDS"));

    internal static TimeSpan ResolveTimeLimit(string? rigSeconds) =>
        int.TryParse(rigSeconds, out var seconds) && seconds is >= 60 and <= 1800
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(30);

    public static readonly int TimeLimitSeconds = checked((int)TimeLimit.TotalSeconds);

    /// <summary>GENERATED: the guide's "within 15 of 20 minutes", scaled to 30.</summary>
    public static readonly TimeSpan FastClear = TimeSpan.FromMinutes(22.5);

    /// <summary>The shared end-of-run leave countdown every dungeon publishes.</summary>
    public static readonly TimeSpan EndExitDelay = TimeSpan.FromSeconds(30);

    /// <summary>the admin: 300 points to clear.</summary>
    public const int TargetScore = 300;

    public const int ElitePoints = 10;

    public const int NormalPoints = 1;

    /// <summary>Elites hold once per run (they come back after an hour, longer
    /// than any run); normals keep the ordinary 10 s respawn so 300 points can
    /// be reached on the small map.</summary>
    public static readonly TimeSpan EliteRespawnInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// The cross-map client templates map 210 may use (key -> the map whose own
    /// Monster.ini and gameplay catalog define it, and its rank). Real client
    /// templates; their use on map 210 is the admin's decision.
    /// </summary>
    public static FrozenDictionary<string, TrainingGroundTemplate> Templates { get; } =
        new TrainingGroundTemplate[]
        {
            new("A_normal_spider_006", 13, false),
            new("A_normal_scorpion_008", 13, false),
            new("A_normal_scorpion_009", 13, false),
            new("A_normal_Spinspiderm_001", 6, false),
            new("A_normal_Spinspiderm_002", 6, false),
            new("B_normalB_spider_004", 14, false),
            new("B_normalB_scorpion_001", 12, false),
            new("A_elite_spider_006", 13, true),
            new("A_elite_scorpion_008", 13, true),
            new("A_elite_spider_007", 3, true),
            new("A_elite_ScorpionNdi_001", 9, true)
        }.ToFrozenDictionary(static template => template.TemplateKey, StringComparer.Ordinal);

    /// <summary>WARRIOR (F-51): any Training Ground map - Heracles (210) or a
    /// Warriors' Training Ground (201/202/203/206). Heracles alone is <see cref="MapId"/>.</summary>
    public static bool IsTrainingGroundMap(int mapId) => TryGetGround(mapId, out _);

    /// <summary>WARRIOR (F-51): the Trials of Heracles as one ground of the shared run.</summary>
    public static TrainingGroundDefinition Heracles { get; } = new(
        "heracles",
        "Trials of Hercules",
        MapId,
        ClientSceneId,
        ArrivalX,
        ArrivalZ,
        TimeLimit,
        FastClear,
        TargetScore,
        InstanceCallerEntryKind.TrainingGround)
    {
        Score = ScoreFor,
        Reward = RewardFor,
        IsEliteKill = IsElite
    };

    /// <summary>WARRIOR (F-51): every ground the shared Training Ground run hosts.</summary>
    public static IReadOnlyList<TrainingGroundDefinition> Grounds { get; } =
        [Heracles, .. WarriorTrainingGroundPolicy.Grounds];

    public static bool TryGetGround(int mapId, out TrainingGroundDefinition ground)
    {
        foreach (var candidate in Grounds)
        {
            if (candidate.MapId == mapId)
            {
                ground = candidate;
                return true;
            }
        }
        ground = null!;
        return false;
    }

    /// <summary>WARRIOR (F-51): the daily-entry kinds every Training Ground uses (10, 21-24).</summary>
    public static bool IsTrainingGroundKind(InstanceCallerEntryKind kind) =>
        kind == InstanceCallerEntryKind.TrainingGround ||
        WarriorTrainingGroundPolicy.IsWarriorKind(kind);

    public static bool IsElite(string templateKey) =>
        Templates.TryGetValue(templateKey, out var template) && template.IsElite;

    /// <summary>the admin: elite 10, normal 1; anything not on the list scores nothing.</summary>
    public static int ScoreFor(string templateKey) =>
        Templates.TryGetValue(templateKey, out var template)
            ? template.IsElite ? ElitePoints : NormalPoints
            : 0;

    /// <summary>
    /// The completion reward (IGG acid=166 Training Ground 2 column; thresholds
    /// GENERATED, scaled to 300 points / 30 minutes).
    /// </summary>
    public static TrainingGroundReward RewardFor(int score, TimeSpan? clearedAfter)
    {
        if (score >= TargetScore && clearedAfter is { } elapsed)
        {
            return elapsed <= FastClear
                ? new(384_000, 16, "completed-fast")
                : new(360_000, 12, "completed");
        }

        return score switch
        {
            >= 240 => new(312_000, 9, "incomplete-240"),
            >= 180 => new(288_000, 6, "incomplete-180"),
            >= 120 => new(240_000, 3, "incomplete-120"),
            _ => new(60_000, 2, "incomplete-0")
        };
    }
}

internal sealed record TrainingGroundTemplate(string TemplateKey, short SourceMapId, bool IsElite);

/// <summary>
/// WARRIOR (F-51): one ground of the shared Training Ground run - the map, its
/// client scene (Enter window, panel, end control), arrival / revive point, clock,
/// points to clear, the daily-entry kind, and how a kill scores and a run pays.
/// </summary>
internal sealed record TrainingGroundDefinition(
    string Key,
    string DisplayName,
    byte MapId,
    int ClientSceneId,
    float ArrivalX,
    float ArrivalZ,
    TimeSpan TimeLimit,
    TimeSpan FastClear,
    int TargetScore,
    InstanceCallerEntryKind EntryKind)
{
    /// <summary>Points of one kill by client template key (0 = does not score).</summary>
    public required Func<string, int> Score { get; init; }

    /// <summary>The completion reward for a score and (when cleared) the clear time.</summary>
    public required Func<int, TimeSpan?, TrainingGroundReward> Reward { get; init; }

    /// <summary>Whether a scoring kill counts as an elite (for the run's tally only).</summary>
    public required Func<string, bool> IsEliteKill { get; init; }

    public int TimeLimitSeconds => checked((int)TimeLimit.TotalSeconds);
}

internal readonly record struct TrainingGroundReward(int Experience, int TalentPoints, string Tier);

internal enum TrainingGroundKillOutcome : byte
{
    Ignored = 0,
    Scored = 1,
    Completed = 2
}

/// <summary>
/// TRAINING: the kills of one run (pure state, guarded by the caller). A kill
/// counts once per (object id, spawn generation), so a replayed settlement never
/// scores twice while a respawned normal (new generation) scores again. The
/// 300th point completes the run and nothing scores after it.
/// </summary>
internal sealed class TrainingGroundRunProgress(TrainingGroundDefinition? ground = null)
{
    private readonly HashSet<(uint ObjectId, uint Generation)> _killed = [];
    private readonly TrainingGroundDefinition _ground = ground ?? TrainingGroundPolicy.Heracles;

    public int Score { get; private set; }

    public int Elites { get; private set; }

    public int Normals { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public TrainingGroundKillOutcome Record(
        uint objectId,
        uint spawnGeneration,
        string templateKey,
        DateTimeOffset at)
    {
        var points = _ground.Score(templateKey);
        if (CompletedAt is not null || points == 0 ||
            !_killed.Add((objectId, spawnGeneration)))
        {
            return TrainingGroundKillOutcome.Ignored;
        }

        Score += points;
        if (_ground.IsEliteKill(templateKey))
        {
            Elites++;
        }
        else
        {
            Normals++;
        }

        if (Score < _ground.TargetScore)
        {
            return TrainingGroundKillOutcome.Scored;
        }

        CompletedAt = at;
        return TrainingGroundKillOutcome.Completed;
    }
}

/// <summary>One member's reward row, written once per (run, character).</summary>
internal sealed record TrainingGroundRewardGrant(
    Guid RunId,
    int CharacterId,
    short RealmId,
    int Score,
    string Outcome,
    int ElapsedSeconds,
    int Experience,
    int TalentPoints);

/// <summary>
/// TRAINING: durable reward rows. One row per (run, character) is the
/// double-claim guard: the registry may write a run's rows more than once, the
/// primary key keeps one; the character's own handler claims a row atomically
/// (claimed_at) before paying it and never puts it back (at most once).
/// </summary>
internal interface ITrainingGroundStore
{
    /// <summary>Idempotent first-use DDL: the reward table, and daily-entry kind
    /// 10 (check constraints widened, settings row 1 free / 0 paid).</summary>
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts the rows (ON CONFLICT DO NOTHING); returns how many were new.</summary>
    Task<int> RecordRewardsAsync(
        IReadOnlyCollection<TrainingGroundRewardGrant> grants,
        CancellationToken cancellationToken = default);

    /// <summary>Marks every unclaimed row of the character claimed and returns them.</summary>
    Task<IReadOnlyList<TrainingGroundRewardGrant>> ClaimRewardsAsync(
        int characterId,
        CancellationToken cancellationToken = default);

    Task MarkPaidAsync(Guid runId, int characterId, CancellationToken cancellationToken = default);
}
