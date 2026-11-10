using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Serializes publication and committed deaths for one exact Atlantis run.
/// Previewing never consumes a wave; binding the same ordered identities is
/// idempotent. A later wave cannot reuse an identity from an earlier wave.
/// Points remain owned by AtlantisRunRuntime, not this progression ledger.
/// </summary>
internal sealed class AtlantisWaveRuntime
{
    private readonly object _gate = new();
    private readonly WorldInstanceId _instanceId;
    private readonly DateTimeOffset _startedAt;
    private readonly DateTimeOffset _deadline;
    private readonly HashSet<(uint ObjectId, uint Generation)> _used = [];
    private readonly Dictionary<(uint ObjectId, uint Generation), AtlantisWaveMonsterIdentity> _remaining = [];
    private ImmutableArray<AtlantisWaveMonsterIdentity> _bound = [];
    private DateTimeOffset _lastObservedAt;
    private DateTimeOffset? _terminalAt;
    private AtlantisWaveState _state = AtlantisWaveState.PendingPublication;
    private int _waveIndex;
    private int _completedWaves;
    private int _committedKills;

    public AtlantisWaveRuntime(WorldInstanceDescriptor descriptor, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!AtlantisEncounterPolicy.IsAtlantisInstance(descriptor) ||
            descriptor.LifecycleState is not
                (WorldInstanceLifecycleState.Creating or WorldInstanceLifecycleState.Active))
        {
            throw new ArgumentException("Atlantis waves require a creating or active map-205 dungeon.",
                nameof(descriptor));
        }
        _instanceId = descriptor.InstanceId;
        _startedAt = startedAt.ToUniversalTime();
        if (_startedAt < descriptor.CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(startedAt),
                "Atlantis waves cannot start before their world instance exists.");
        }
        _deadline = _startedAt.Add(AtlantisEncounterPolicy.TimeLimit);
        _lastObservedAt = _startedAt;
    }

    public AtlantisWaveSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotCore();
        }
    }

    public AtlantisWavePublication? PreviewPendingWave(DateTimeOffset now)
    {
        lock (_gate)
        {
            var at = now.ToUniversalTime();
            if (at < _lastObservedAt)
            {
                return null;
            }
            AdvanceCore(at);
            return _state == AtlantisWaveState.PendingPublication
                ? new(new(_instanceId, _waveIndex), AtlantisWavePlan.Waves[_waveIndex])
                : null;
        }
    }

    public AtlantisWaveBindResult BindPendingWave(AtlantisWaveToken token,
        IReadOnlyList<AtlantisWaveMonsterIdentity> identities, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var candidate = identities.ToImmutableArray();
        lock (_gate)
        {
            if (token.WorldInstanceId != _instanceId)
            {
                return BindResult(AtlantisWaveBindOutcome.WrongInstance);
            }
            if (IsTerminal)
            {
                return BindResult(AtlantisWaveBindOutcome.RunNotActive);
            }
            var at = now.ToUniversalTime();
            if (at < _lastObservedAt)
            {
                return BindResult(AtlantisWaveBindOutcome.TimestampMovedBackward);
            }
            AdvanceCore(at);
            if (_state == AtlantisWaveState.TimedOut)
            {
                return BindResult(AtlantisWaveBindOutcome.TimedOut);
            }
            if (token.WaveIndex != _waveIndex)
            {
                return BindResult(AtlantisWaveBindOutcome.WrongWave);
            }
            if (_state == AtlantisWaveState.Active)
            {
                return BindResult(_bound.SequenceEqual(candidate)
                    ? AtlantisWaveBindOutcome.AlreadyBound : AtlantisWaveBindOutcome.BindingConflict);
            }

            var slots = AtlantisWavePlan.Waves[_waveIndex].Slots;
            var keys = new HashSet<(uint ObjectId, uint Generation)>();
            if (candidate.Length != slots.Length || candidate.Where((identity, index) =>
                    identity.ObjectId == 0 || identity.SpawnGeneration == 0 ||
                    identity.Rank != slots[index].Rank ||
                    !keys.Add((identity.ObjectId, identity.SpawnGeneration))).Any())
            {
                return BindResult(AtlantisWaveBindOutcome.InvalidMonsterBindings);
            }
            if (keys.Any(_used.Contains))
            {
                return BindResult(AtlantisWaveBindOutcome.IdentityReused);
            }

            _bound = candidate;
            foreach (var identity in _bound)
            {
                var key = (identity.ObjectId, identity.SpawnGeneration);
                _used.Add(key);
                _remaining.Add(key, identity);
            }
            _state = AtlantisWaveState.Active;
            return BindResult(AtlantisWaveBindOutcome.Bound);
        }
    }

    public bool TryGetActiveMonster(WorldInstanceId expectedInstanceId,
        uint objectId, uint spawnGeneration, out AtlantisWaveMonsterIdentity identity)
    {
        lock (_gate)
        {
            identity = default;
            return expectedInstanceId == _instanceId && _state == AtlantisWaveState.Active &&
                _remaining.TryGetValue((objectId, spawnGeneration), out identity);
        }
    }

    public AtlantisWaveKillResult RecordCommittedKill(WorldInstanceId expectedInstanceId,
        uint objectId, uint spawnGeneration, DateTimeOffset committedAt)
    {
        lock (_gate)
        {
            if (expectedInstanceId != _instanceId)
            {
                return KillResult(AtlantisWaveKillOutcome.WrongInstance);
            }
            if (IsTerminal)
            {
                return KillResult(AtlantisWaveKillOutcome.RunNotActive);
            }
            var at = committedAt.ToUniversalTime();
            if (at < _lastObservedAt)
            {
                return KillResult(AtlantisWaveKillOutcome.TimestampMovedBackward);
            }
            AdvanceCore(at);
            if (_state == AtlantisWaveState.TimedOut)
            {
                return KillResult(AtlantisWaveKillOutcome.TimedOut);
            }
            if (_state != AtlantisWaveState.Active)
            {
                return KillResult(AtlantisWaveKillOutcome.NoActiveWave);
            }
            if (!_remaining.Remove((objectId, spawnGeneration)))
            {
                return KillResult(_bound.Any(identity => identity.ObjectId == objectId &&
                        identity.SpawnGeneration == spawnGeneration)
                    ? AtlantisWaveKillOutcome.DuplicateKill : AtlantisWaveKillOutcome.UnknownMonster);
            }

            _committedKills++;
            if (_remaining.Count != 0)
            {
                return KillResult(AtlantisWaveKillOutcome.Applied);
            }
            _completedWaves++;
            if (_completedWaves == AtlantisWavePlan.WaveCount)
            {
                _state = AtlantisWaveState.Completed;
                _terminalAt = at;
                return KillResult(AtlantisWaveKillOutcome.RunCompleted);
            }
            _waveIndex++;
            _bound = [];
            _state = AtlantisWaveState.PendingPublication;
            return KillResult(AtlantisWaveKillOutcome.WaveCleared);
        }
    }

    public AtlantisWaveSnapshot Advance(DateTimeOffset now)
    {
        lock (_gate)
        {
            AdvanceCore(now.ToUniversalTime());
            return SnapshotCore();
        }
    }

    public AtlantisWaveSnapshot Cancel(DateTimeOffset now)
    {
        lock (_gate)
        {
            var at = now.ToUniversalTime();
            AdvanceCore(at);
            if (!IsTerminal)
            {
                _state = AtlantisWaveState.Cancelled;
                _terminalAt = _lastObservedAt;
            }
            return SnapshotCore();
        }
    }

    private bool IsTerminal => _state is AtlantisWaveState.Completed or
        AtlantisWaveState.TimedOut or AtlantisWaveState.Cancelled;

    private void AdvanceCore(DateTimeOffset now)
    {
        if (IsTerminal || now < _lastObservedAt)
        {
            return;
        }
        if (now >= _deadline)
        {
            _lastObservedAt = _deadline;
            _terminalAt = _deadline;
            _state = AtlantisWaveState.TimedOut;
            return;
        }
        _lastObservedAt = now;
    }

    private AtlantisWaveSnapshot SnapshotCore() => new(
        _instanceId, _startedAt, _deadline, _lastObservedAt, _state, _waveIndex,
        _completedWaves, _committedKills, _remaining.Count, _bound, _terminalAt);

    private AtlantisWaveBindResult BindResult(AtlantisWaveBindOutcome outcome) => new(outcome, SnapshotCore());
    private AtlantisWaveKillResult KillResult(AtlantisWaveKillOutcome outcome) => new(outcome, SnapshotCore());
}
