using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>Exact-instance, monotonic, generation-fenced eight-island progression.</summary>
internal sealed class WonderlandRunRuntime
{
    private readonly object _gate = new();
    private readonly WorldInstanceId _instanceId;
    private readonly DateTimeOffset _startedAt;
    private readonly DateTimeOffset _deadline;
    private ImmutableArray<WonderlandParticipant> _party;
    private readonly byte _partyCamp;
    private readonly ImmutableArray<WonderlandSpawnPolicy>[] _plans;
    private readonly HashSet<WonderlandMonsterIdentity> _bound = [];
    private readonly HashSet<WonderlandMonsterIdentity> _dead = [];
    private readonly List<WonderlandIslandClear> _clears = [];
    private WonderlandRunState _state = WonderlandRunState.Active;
    private DateTimeOffset _lastObservedAt;
    private DateTimeOffset _stageEnteredAt;
    private DateTimeOffset? _terminalAt;
    private int _island = 1;
    private int _remaining;
    private bool _pending = true;

    public WonderlandRunRuntime(WorldInstanceDescriptor descriptor,
        IReadOnlyList<WonderlandParticipant> party, DateTimeOffset startedAt, byte? fixedPartyCamp = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!WonderlandEncounterPolicy.IsWonderlandInstance(descriptor) ||
            descriptor.LifecycleState is not (WorldInstanceLifecycleState.Creating or WorldInstanceLifecycleState.Active))
            throw new ArgumentException("Wonderland requires a creating or active map-207 dungeon.", nameof(descriptor));
        _party = WonderlandEncounterPolicy.ValidateParty(party);
        _partyCamp = fixedPartyCamp ?? _party[0].Camp;
        _startedAt = startedAt.ToUniversalTime();
        if (_startedAt < descriptor.CreatedAt)
            throw new ArgumentOutOfRangeException(nameof(startedAt));
        _instanceId = descriptor.InstanceId;
        _deadline = _startedAt.Add(WonderlandEncounterPolicy.TimeLimit);
        _lastObservedAt = _stageEnteredAt = _startedAt;
        _plans = Enumerable.Range(1, 8).Select(i =>
            WonderlandMonsterPlan.Create(i, _party.Length, _partyCamp)).ToArray();
    }

    public WonderlandSnapshot Snapshot() { lock (_gate) return SnapshotCore(); }

    public bool TryBindStage(int island, IReadOnlyList<WonderlandMonsterIdentity> identities,
        DateTimeOffset now, out WonderlandSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(identities);
        lock (_gate)
        {
            snapshot = SnapshotCore();
            if (now < _lastObservedAt) return false;
            AdvanceCore(now);
            snapshot = SnapshotCore();
            if (_state != WonderlandRunState.Active || island != _island) return false;
            var candidate = identities.ToHashSet();
            var plan = _plans[_island - 1];
            if (!_pending) return candidate.Count == identities.Count && candidate.Count == plan.Length &&
                plan.All(p => candidate.Contains(new(p.ObjectId, 1)));
            if (candidate.Count != identities.Count || candidate.Count != plan.Length ||
                plan.Any(p => !candidate.Contains(new(p.ObjectId, 1)))) return false;
            _bound.UnionWith(candidate);
            _remaining = plan.Count(p => p.RequiredForProgression);
            _pending = false;
            _stageEnteredAt = _lastObservedAt;
            snapshot = SnapshotCore();
            return true;
        }
    }

    public WonderlandKillResult RecordCommittedKill(WorldInstanceId instanceId, uint objectId,
        uint generation, DateTimeOffset now)
    {
        lock (_gate)
        {
            WonderlandKillResult Result(WonderlandKillOutcome outcome, WonderlandIslandClear? clear = null) =>
                new(outcome, SnapshotCore(), clear);
            if (instanceId != _instanceId) return Result(WonderlandKillOutcome.WrongInstance);
            if (now < _lastObservedAt) return Result(WonderlandKillOutcome.StaleTimestamp);
            AdvanceCore(now);
            if (_state != WonderlandRunState.Active) return Result(WonderlandKillOutcome.RunNotActive);
            var identity = new WonderlandMonsterIdentity(objectId, generation);
            if (_dead.Contains(identity)) return Result(WonderlandKillOutcome.Duplicate);
            if (!_bound.Contains(identity)) return Result(_pending
                ? WonderlandKillOutcome.NotPublished : WonderlandKillOutcome.UnknownMonster);
            var policy = _plans.SelectMany(plan => plan).Single(p => p.ObjectId == objectId);
            if (policy.IsAllied) return Result(WonderlandKillOutcome.UnknownMonster);
            if (policy.Stage == 8 && policy.Role == WonderlandMonsterRole.ChestGuard && _remaining > 1)
                return Result(WonderlandKillOutcome.UnknownMonster);
            _dead.Add(identity);
            if (policy.Stage != _island || !policy.RequiredForProgression || --_remaining != 0)
                return Result(WonderlandKillOutcome.Applied);
            var clear = new WonderlandIslandClear(_island, _lastObservedAt);
            _clears.Add(clear);
            if (_island == 8)
            {
                _state = WonderlandRunState.Completed;
                _terminalAt = _lastObservedAt;
                return Result(WonderlandKillOutcome.RunCompleted, clear);
            }
            _island++;
            _pending = true;
            return Result(WonderlandKillOutcome.IslandCleared, clear);
        }
    }

    public WonderlandSnapshot Advance(DateTimeOffset now)
    {
        lock (_gate) { AdvanceCore(now); return SnapshotCore(); }
    }

    /// <summary>
    /// Adds a member who confirmed their entry after the run was sealed, so the
    /// run's own roster - island travel, allied combat presentation and area
    /// targeting - covers them.
    /// </summary>
    /// <remarks>
    /// The monster plan is deliberately left exactly as it was built from the
    /// original roster: re-planning it would change live monster counts and
    /// health mid-run. The late member fights the monsters that are already
    /// published.
    /// </remarks>
    public bool TryAddLateParticipant(
        WonderlandParticipant participant,
        out WonderlandSnapshot snapshot)
    {
        lock (_gate)
        {
            snapshot = SnapshotCore();
            if (_state != WonderlandRunState.Active ||
                participant.Camp != _partyCamp ||
                _party.Length >= 5 ||
                _party.Any(member => member.CharacterId == participant.CharacterId))
            {
                return false;
            }
            _party = _party.Add(participant);
            snapshot = SnapshotCore();
            return true;
        }
    }

    public WonderlandSnapshot Cancel(DateTimeOffset now)
    {
        lock (_gate)
        {
            AdvanceCore(now);
            if (_state == WonderlandRunState.Active)
            {
                _state = WonderlandRunState.Cancelled;
                _terminalAt = _lastObservedAt;
            }
            return SnapshotCore();
        }
    }

    private void AdvanceCore(DateTimeOffset now)
    {
        if (_state != WonderlandRunState.Active || now < _lastObservedAt) return;
        _lastObservedAt = now >= _deadline ? _deadline : now.ToUniversalTime();
        if (now >= _deadline)
        {
            _state = WonderlandRunState.TimedOut;
            _terminalAt = _deadline;
        }
    }

    private WonderlandSnapshot SnapshotCore() => new(_instanceId, _startedAt, _deadline,
        _lastObservedAt, _state, _island, _clears.Count, _clears.LastOrDefault()?.ClearedAt,
        _terminalAt, _stageEnteredAt, _partyCamp, _pending, _remaining, _party,
        _plans[_island - 1], _clears.ToImmutableArray());
}
