using System.Collections.Concurrent;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record MonsterVisibilityDelta(
    WorldGridCell PlayerCell,
    IReadOnlyList<MonsterRuntimeSnapshot> Entering,
    IReadOnlyList<uint> Leaving);

internal sealed class MonsterVisibilityTransition : IAsyncDisposable
{
    private MonsterViewerState? _viewer;
    private readonly IReadOnlyDictionary<uint, MonsterAppearanceVersion> _desiredVersions;
    private readonly Dictionary<uint, MonsterAppearanceVersion> _publishedHealthVersions = [];

    public MonsterVisibilityTransition(
        MonsterViewerState viewer,
        MonsterVisibilityDelta delta,
        IReadOnlyDictionary<uint, MonsterAppearanceVersion> desiredVersions)
    {
        _viewer = viewer;
        Delta = delta;
        _desiredVersions = desiredVersions;
    }

    public MonsterVisibilityDelta Delta { get; }

    public bool IsDesiredVisible(uint objectId)
    {
        return _desiredVersions.ContainsKey(objectId);
    }

    public bool NeedsAbsoluteHealthPublication(MonsterRuntimeSnapshot monster)
    {
        var viewer = _viewer ??
            throw new ObjectDisposedException(
                nameof(MonsterVisibilityTransition));
        return _desiredVersions.TryGetValue(
                monster.ObjectId,
                out var desired) &&
            desired.SpawnGeneration == monster.SpawnGeneration &&
            viewer.VisibleMonsterVersions.TryGetValue(
                monster.ObjectId,
                out var visible) &&
            visible.SpawnGeneration == monster.SpawnGeneration &&
            visible.HealthRevision < monster.HealthRevision;
    }

    public void RecordAbsoluteHealthPublication(MonsterRuntimeSnapshot monster)
    {
        if (!NeedsAbsoluteHealthPublication(monster))
        {
            throw new InvalidOperationException(
                "A health refresh must advance a visible generation.");
        }

        // Call only after sending the absolute HP packet while this transition
        // holds the viewer gate. Older queued damage is then safely obsolete.
        _publishedHealthVersions[monster.ObjectId] = monster.AppearanceVersion;
    }

    public void Commit()
    {
        var viewer = _viewer ??
            throw new ObjectDisposedException(
                nameof(MonsterVisibilityTransition));
        foreach (var objectId in viewer.VisibleMonsterVersions.Keys)
        {
            if (!_desiredVersions.ContainsKey(objectId))
            {
                viewer.VisibleMonsterVersions.TryRemove(objectId, out _);
            }
        }

        // Only an appearance actually sent by this transition may advance the
        // viewer's health revision. Merely observing a newer runtime snapshot
        // during unrelated AOI work must not suppress a pending delta.
        foreach (var monster in Delta.Entering)
        {
            viewer.VisibleMonsterVersions[monster.ObjectId] =
                monster.AppearanceVersion;
        }

        foreach (var publication in _publishedHealthVersions)
        {
            viewer.VisibleMonsterVersions[publication.Key] = publication.Value;
        }

        viewer.PlayerCell = Delta.PlayerCell;
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    internal void Release()
    {
        var viewer = Interlocked.Exchange(ref _viewer, null);
        viewer?.TransitionGate.Release();
    }
}

internal sealed class MonsterViewerState
{
    public SemaphoreSlim TransitionGate { get; } = new(1, 1);

    public ConcurrentDictionary<uint, MonsterAppearanceVersion>
        VisibleMonsterVersions { get; } = [];

    public WorldGridCell? PlayerCell { get; set; }
}

internal sealed class MonsterViewerDeliveryLease : IAsyncDisposable
{
    private MonsterViewerState? _viewer;
    private readonly IReadOnlyList<MonsterHealthMutation>
        _directHealthMutations;
    private readonly IReadOnlyList<uint> _reconciliationObjectIds;
    private readonly IReadOnlyList<uint> _terminalObjectIds;
    private readonly List<MonsterRuntimeSnapshot> _requiredSourceAppearances = [];

    public MonsterViewerDeliveryLease(
        MonsterViewerState viewer,
        IReadOnlyList<MonsterHealthMutation> directHealthMutations,
        IReadOnlyList<uint> reconciliationObjectIds,
        IReadOnlyList<MonsterRuntimeSnapshot> reconciliationMonsters,
        IReadOnlyList<uint> terminalObjectIds)
    {
        _viewer = viewer;
        _directHealthMutations = directHealthMutations;
        _reconciliationObjectIds = reconciliationObjectIds;
        ReconciliationMonsters = reconciliationMonsters;
        _terminalObjectIds = terminalObjectIds;
    }

    public IReadOnlyList<MonsterHealthMutation> DirectHealthMutations =>
        _directHealthMutations;

    public IReadOnlyList<uint> ReconciliationObjectIds =>
        _reconciliationObjectIds;

    public IReadOnlyList<uint> TerminalObjectIds => _terminalObjectIds;

    public IReadOnlyList<MonsterRuntimeSnapshot> RequiredSourceAppearances =>
        _requiredSourceAppearances;

    // A damage source may be just outside the viewer's ordinary AOI while its
    // target is visible. Hydrate it in the same ordered batch and under this
    // existing visibility gate; acquiring a second lease here would deadlock.
    public void IncludeRequiredSourceAppearance(MonsterRuntimeSnapshot source)
    {
        var viewer = _viewer ??
            throw new ObjectDisposedException(
                nameof(MonsterViewerDeliveryLease));
        if (viewer.VisibleMonsterVersions.TryGetValue(
                source.ObjectId,
                out var visible) &&
            visible.SpawnGeneration == source.SpawnGeneration ||
            _requiredSourceAppearances.Any(
                appearance => appearance.ObjectId == source.ObjectId))
        {
            return;
        }

        _requiredSourceAppearances.Add(source);
    }

    public IReadOnlyList<MonsterRuntimeSnapshot> ReconciliationMonsters
    {
        get;
    }

    public void Commit()
    {
        var viewer = _viewer ??
            throw new ObjectDisposedException(
                nameof(MonsterViewerDeliveryLease));
        var reconciledVersions = ReconciliationMonsters.ToDictionary(
            monster => monster.ObjectId,
            monster => monster.AppearanceVersion);
        foreach (var objectId in _reconciliationObjectIds)
        {
            if (reconciledVersions.TryGetValue(objectId, out var version))
            {
                viewer.VisibleMonsterVersions[objectId] = version;
            }
            else
            {
                viewer.VisibleMonsterVersions.TryRemove(objectId, out _);
            }
        }

        foreach (var mutation in _directHealthMutations)
        {
            viewer.VisibleMonsterVersions[mutation.ObjectId] =
                mutation.AfterVersion;
        }

        foreach (var source in _requiredSourceAppearances)
        {
            viewer.VisibleMonsterVersions[source.ObjectId] =
                source.AppearanceVersion;
        }
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    internal void Release()
    {
        var viewer = Interlocked.Exchange(ref _viewer, null);
        viewer?.TransitionGate.Release();
    }
}
