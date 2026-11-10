using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Process-local team score and clock for one exact Atlantis dungeon. Committed
/// death delivery and the world clock may race; a single lock linearizes them.
/// This runtime does not grant rewards or mutate monsters or player membership.
/// </summary>
internal sealed class AtlantisRunRuntime
{
    private readonly object _gate = new();
    private readonly HashSet<(uint ObjectId, uint SpawnGeneration)> _scoredKills = [];
    private readonly WorldInstanceId _worldInstanceId;
    private readonly DateTimeOffset _startedAt;
    private readonly DateTimeOffset _deadline;
    private DateTimeOffset _lastObservedAt;
    private AtlantisRunState _state = AtlantisRunState.Active;
    private int _teamPoints;
    private DateTimeOffset? _terminalAt;

    public AtlantisRunRuntime(WorldInstanceDescriptor descriptor, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!AtlantisEncounterPolicy.IsAtlantisInstance(descriptor) ||
            descriptor.LifecycleState is not
                (WorldInstanceLifecycleState.Creating or WorldInstanceLifecycleState.Active))
        {
            throw new ArgumentException(
                "An Atlantis run requires a creating or active map-205 dungeon.", nameof(descriptor));
        }

        _worldInstanceId = descriptor.InstanceId;
        _startedAt = startedAt.ToUniversalTime();
        if (_startedAt < descriptor.CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(startedAt),
                "An Atlantis run cannot start before its world instance exists.");
        }
        _deadline = _startedAt.Add(AtlantisEncounterPolicy.TimeLimit);
        _lastObservedAt = _startedAt;
    }

    public AtlantisRunSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotCore();
        }
    }

    public AtlantisRunSnapshot Advance(DateTimeOffset now)
    {
        lock (_gate)
        {
            AdvanceCore(now.ToUniversalTime());
            return SnapshotCore();
        }
    }

    public AtlantisKillResult RecordCommittedMonsterKill(
        WorldInstanceId expectedInstanceId,
        uint objectId,
        uint spawnGeneration,
        AtlantisMonsterRank rank,
        DateTimeOffset committedAt)
    {
        lock (_gate)
        {
            if (expectedInstanceId != _worldInstanceId)
            {
                return Result(AtlantisKillOutcome.WrongInstance);
            }
            if (_state != AtlantisRunState.Active)
            {
                return Result(AtlantisKillOutcome.RunNotActive);
            }

            var observedAt = committedAt.ToUniversalTime();
            if (observedAt < _lastObservedAt)
            {
                return Result(AtlantisKillOutcome.TimestampMovedBackward);
            }
            AdvanceCore(observedAt);
            if (_state == AtlantisRunState.TimedOut)
            {
                return Result(AtlantisKillOutcome.TimedOut);
            }
            if (!AtlantisEncounterPolicy.TryGetKillPoints(rank, out var points))
            {
                return Result(AtlantisKillOutcome.UnknownRank);
            }
            if (objectId == 0 || spawnGeneration == 0)
            {
                return Result(AtlantisKillOutcome.InvalidMonsterIdentity);
            }
            if (!_scoredKills.Add((objectId, spawnGeneration)))
            {
                return Result(AtlantisKillOutcome.DuplicateKill);
            }

            // At least one point per unique kill bounds this set to 850 entries:
            // the first threshold crossing terminalizes and freezes the run.
            _teamPoints += points;
            if (_teamPoints >= AtlantisEncounterPolicy.CompletionTeamPoints)
            {
                _state = AtlantisRunState.Completed;
                _terminalAt = observedAt;
            }
            return Result(_state == AtlantisRunState.Completed
                ? AtlantisKillOutcome.Completed : AtlantisKillOutcome.Applied, points);
        }
    }

    public AtlantisRunSnapshot Cancel(DateTimeOffset now)
    {
        lock (_gate)
        {
            AdvanceCore(now.ToUniversalTime());
            if (_state == AtlantisRunState.Active)
            {
                _state = AtlantisRunState.Cancelled;
                _terminalAt = _lastObservedAt;
            }
            return SnapshotCore();
        }
    }

    private void AdvanceCore(DateTimeOffset now)
    {
        if (_state != AtlantisRunState.Active || now < _lastObservedAt)
        {
            return;
        }
        if (now >= _deadline)
        {
            _lastObservedAt = _deadline;
            _terminalAt = _deadline;
            _state = AtlantisRunState.TimedOut;
            return;
        }
        _lastObservedAt = now;
    }

    private AtlantisKillResult Result(AtlantisKillOutcome outcome, int points = 0) =>
        new(outcome, points, SnapshotCore());

    private AtlantisRunSnapshot SnapshotCore() => new(
        _worldInstanceId, AtlantisEncounterPolicy.ContentMapId, _startedAt,
        _deadline, _lastObservedAt, _state, _teamPoints, _scoredKills.Count, _terminalAt);
}
