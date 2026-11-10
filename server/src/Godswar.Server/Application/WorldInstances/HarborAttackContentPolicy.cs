using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// INSTANCES: Bay Under Attack (港湾遇袭) run content rules.
/// </summary>
/// <remarks>
/// <para>
/// Retail (IGG guide acid=201): a party of 3-5 at level 50+, one entry a day,
/// 30 minutes, no early exit, revive at the teleport point, every monster drops
/// treasure, and killing the end boss Christina ends the instance. Those rules
/// live in <see cref="HarborAttackPolicy"/>, the Instance Caller entry and here.
/// </para>
/// <para>
/// GENERATED (not in the guide): the team score of 1 per normal, 10 per elite and
/// 50 per boss (the same weights the Atlantis panel uses), shown in the client's
/// own repetition panel. The monsters themselves are generated
/// <c>gm_monster_spawns</c> rows (Development/build/instances/content).
/// </para>
/// </remarks>
internal static class HarborAttackContentPolicy
{
    /// <summary>Christina, the end boss, on both level bands (client Monster.ini keys).</summary>
    public static bool IsEndBoss(string templateKey) =>
        templateKey is "B_bossSA_mage_018" or "B_bossSAQ_mage_018";

    /// <summary>GENERATED team-score weight of one kill.</summary>
    public static int ScoreFor(string templateKey) =>
        templateKey.StartsWith("B_boss", StringComparison.Ordinal) ? 50
        : templateKey.StartsWith("B_elite", StringComparison.Ordinal) ? 10
        : 1;

    public static bool IsHarborMap(int mapId) =>
        DynamicDungeonContentMapPolicy.IsHarborAttackMap(mapId);
}

/// <summary>
/// INSTANCES: the kills of one Bay Under Attack run (pure state, guarded by the
/// caller). A kill counts once per (object id, spawn generation); Christina's
/// kill completes the run and nothing scores after it.
/// </summary>
internal sealed class HarborAttackRunProgress
{
    private readonly HashSet<(uint ObjectId, uint Generation)> _killed = [];

    public int Score { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Earliest time the tick may scan the map for a dead end boss again.</summary>
    public DateTimeOffset NextEndBossScanAt { get; set; }

    public HarborAttackKillOutcome Record(
        uint objectId,
        uint spawnGeneration,
        string templateKey,
        DateTimeOffset at)
    {
        if (CompletedAt is not null || !_killed.Add((objectId, spawnGeneration)))
        {
            return HarborAttackKillOutcome.Ignored;
        }
        Score += HarborAttackContentPolicy.ScoreFor(templateKey);
        if (!HarborAttackContentPolicy.IsEndBoss(templateKey))
        {
            return HarborAttackKillOutcome.Scored;
        }
        CompletedAt = at;
        return HarborAttackKillOutcome.Completed;
    }
}

internal enum HarborAttackKillOutcome : byte
{
    Ignored = 0,
    Scored = 1,
    Completed = 2
}

internal readonly record struct HarborAttackRefundResult(
    int RefundedEntries,
    int InterruptedRuns,
    int CompletedRuns,
    int ExpiredRuns);

/// <summary>
/// INSTANCES: durable record of every Bay Under Attack run, so a run that a
/// server stop interrupted can give its members their daily entry back.
/// </summary>
/// <remarks>
/// PROJECT RULE, not retail for this instance: it extends the GodsArena wiki-69
/// "Instance Entry Rollback System", whose list names Demeter's Garden, Medusa,
/// Medusa II, Atlantis Portal and Wonderland but NOT Bay Under Attack, and it
/// drops the wiki's "log in within one hour" condition. The IGG guide says
/// "each entry counts". Off by default (instance.harbor_attack_restart_refund
/// = 0) until the admin decides.
/// </remarks>
internal interface IHarborAttackRunLedger
{
    /// <summary>Opens the run row and records the leader party's characters
    /// (they entered on the run's own reservation).</summary>
    Task OpenAsync(
        Guid reservationId,
        short realmId,
        byte mapId,
        DateTimeOffset startedAtUtc,
        IReadOnlyCollection<int> characterIds,
        CancellationToken cancellationToken = default);

    /// <summary>Records a late joiner's own daily-entry reservation against the run.</summary>
    Task AddMemberAsync(
        Guid runReservationId,
        Guid memberReservationId,
        int characterId,
        CancellationToken cancellationToken = default);

    Task CloseAsync(
        Guid reservationId,
        string reason,
        DateTimeOffset closedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes every run that was still open when this process started (it
    /// cannot belong to this process): 'completed' when Christina's death reward
    /// committed for it, else 'refunded' (entries given back) when
    /// <paramref name="refundEntries"/> is set, else 'interrupted'.
    /// </summary>
    Task<HarborAttackRefundResult> RefundInterruptedAsync(
        short realmId,
        DateTimeOffset processStartedAtUtc,
        bool refundEntries,
        CancellationToken cancellationToken = default);
}
