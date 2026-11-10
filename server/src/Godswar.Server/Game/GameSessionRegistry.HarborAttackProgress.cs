using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

/// <summary>
/// INSTANCES: Bay Under Attack run progress - committed kills, the team score,
/// Christina's death ending the run, and the durable run ledger that lets a
/// server stop refund the daily entry.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<WorldInstanceId, HarborAttackRunProgress>
        _harborAttackProgress = [];
    private HarborAttackRunLedgerWriter? _harborAttackLedgerWriter;

    internal void ConfigureHarborAttackRunLedger(IHarborAttackRunLedger? ledger)
    {
        if (ledger is not null)
        {
            Interlocked.CompareExchange(
                ref _harborAttackLedgerWriter,
                new HarborAttackRunLedgerWriter(ledger),
                null);
        }
    }

    /// <summary>
    /// The kill recorder for a monster that died inside a Bay Under Attack run,
    /// or null for every other map. Invoked once the reward settlement committed;
    /// a replayed settlement records nothing twice (keyed by object id and spawn
    /// generation, and harbor monsters never respawn).
    /// </summary>
    internal Action? CaptureHarborAttackMonsterKill(
        ClientSession session,
        MonsterDamageResult damage)
    {
        if (!damage.Killed ||
            !HarborAttackContentPolicy.IsHarborMap(damage.Monster.Definition.MapId) ||
            !_sessions.TryGetValue(session, out var context) ||
            context.MapId != damage.Monster.Definition.MapId ||
            !_harborAttackAdmissions.ContainsKey(context.WorldInstanceId))
        {
            return null;
        }

        var instanceId = context.WorldInstanceId;
        var objectId = damage.ObjectId;
        var generation = damage.Monster.SpawnGeneration;
        var templateKey = damage.Monster.Definition.TemplateKey;
        return () => RecordHarborAttackKill(
            instanceId, objectId, generation, templateKey, DateTimeOffset.UtcNow);
    }

    internal void RecordHarborAttackKill(
        WorldInstanceId instanceId,
        uint objectId,
        uint spawnGeneration,
        string templateKey,
        DateTimeOffset at)
    {
        if (!_harborAttackAdmissions.TryGetValue(instanceId, out var admission))
        {
            return;
        }

        var progress = _harborAttackProgress.GetOrAdd(instanceId, static _ => new());
        HarborAttackKillOutcome outcome;
        lock (progress)
        {
            outcome = progress.Record(objectId, spawnGeneration, templateKey, at);
        }
        if (outcome == HarborAttackKillOutcome.Ignored)
        {
            return;
        }
        if (outcome == HarborAttackKillOutcome.Completed)
        {
            // Durable as soon as possible: Christina's loot is already committed,
            // so the ledger row is closed now, not when the next tick sees it.
            CloseHarborAttackLedger(admission.ReservationId, "completed");
        }
        Console.WriteLine(
            $"[harbor] kill instance={instanceId} object={objectId} " +
            $"template={templateKey} score={HarborAttackScore(instanceId)}" +
            (outcome == HarborAttackKillOutcome.Completed
                ? " christina=defeated" : string.Empty));
    }

    /// <summary>
    /// Fallback for a Christina death whose reward settlement did not commit
    /// (the kill recorder never ran): harbor monsters never respawn, so a dead
    /// end boss on the run's map completes the run. Scans at most every 5 s.
    /// </summary>
    private void DetectHarborAttackEndBossDeath(
        WorldInstanceId instanceId,
        WorldInstanceRuntime runtime,
        DateTimeOffset now)
    {
        var progress = _harborAttackProgress.GetOrAdd(instanceId, static _ => new());
        lock (progress)
        {
            if (progress.CompletedAt is not null || now < progress.NextEndBossScanAt)
            {
                return;
            }
            progress.NextEndBossScanAt = now.AddSeconds(5);
        }
        foreach (var monster in runtime.Map.SnapshotMonsters())
        {
            if (HarborAttackContentPolicy.IsEndBoss(monster.Definition.TemplateKey) &&
                !monster.IsAlive &&
                monster.CurrentHealth == 0 &&
                monster.RespawnAt is null)
            {
                Console.WriteLine(
                    $"[harbor] end boss found dead without a recorded kill instance={instanceId} " +
                    $"object={monster.Definition.ObjectId}");
                RecordHarborAttackKill(
                    instanceId,
                    monster.Definition.ObjectId,
                    monster.SpawnGeneration,
                    monster.Definition.TemplateKey,
                    now);
                return;
            }
        }
    }

    internal int HarborAttackScore(WorldInstanceId instanceId)
    {
        if (!_harborAttackProgress.TryGetValue(instanceId, out var progress))
        {
            return 0;
        }
        lock (progress)
        {
            return progress.Score;
        }
    }

    private bool TryGetHarborAttackCompletion(
        WorldInstanceId instanceId,
        out DateTimeOffset completedAt)
    {
        completedAt = default;
        if (!_harborAttackProgress.TryGetValue(instanceId, out var progress))
        {
            return false;
        }
        lock (progress)
        {
            if (progress.CompletedAt is not { } at)
            {
                return false;
            }
            completedAt = at;
            return true;
        }
    }

    private void OpenHarborAttackLedger(
        Guid reservationId,
        WorldInstanceRuntime runtime,
        DateTimeOffset startedAt,
        IReadOnlyCollection<int> characterIds) =>
        _harborAttackLedgerWriter?.Open(
            reservationId,
            checked((short)runtime.RealmId.Value),
            checked((byte)runtime.MapId),
            startedAt.ToUniversalTime(),
            characterIds);

    /// <summary>
    /// A late joiner's own daily-entry reservation, recorded against the run so
    /// a restart refund gives it back too. No-op outside Bay Under Attack.
    /// </summary>
    internal void RecordHarborAttackMemberReservation(
        WorldInstanceId instanceId,
        Guid memberReservationId,
        int characterId)
    {
        if (_harborAttackLedgerWriter is { } writer &&
            _harborAttackAdmissions.TryGetValue(instanceId, out var admission))
        {
            writer.AddMember(admission.ReservationId, memberReservationId, characterId);
        }
        if (_harborAttackAdmissions.ContainsKey(instanceId))
        {
            RememberHarborAttackMemberReservation(instanceId, memberReservationId, characterId); // BAY2
        }
    }

    /// <summary>Closes the ledger row once per run (ordered after its open, retried).</summary>
    private void CloseHarborAttackLedger(Guid reservationId, string reason) =>
        _harborAttackLedgerWriter?.Close(reservationId, reason, DateTimeOffset.UtcNow);

    private void ForgetHarborAttackProgress(WorldInstanceId instanceId) =>
        _harborAttackProgress.TryRemove(instanceId, out _);
}
