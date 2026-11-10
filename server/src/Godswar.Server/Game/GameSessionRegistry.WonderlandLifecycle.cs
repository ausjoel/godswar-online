using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<WorldInstanceId, byte> _wonderlandDepartures = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte> _wonderlandRetirements = [];
    private readonly ConcurrentDictionary<WorldInstanceId, DateTimeOffset> _wonderlandLastError = [];
    private int _wonderlandWorldInFlight;

    private async Task AdvanceWonderlandWorldAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _wonderlandWorldInFlight, 1) != 0) return;
        try
        {
            await RetryWonderlandTitlesAsync(now, cancellationToken);
            foreach (var entry in _wonderlandAdmissions)
            {
                var instanceId = entry.Key;
                try
                {
                    if (!WorldInstances.TryFind(instanceId, out var runtime))
                    {
                        if (!HasPendingWonderlandTitles(instanceId)) ForgetWonderlandRun(instanceId);
                        continue;
                    }
                    if (runtime.Descriptor.LifecycleState != WorldInstanceLifecycleState.Active)
                    {
                        await TryRetireWonderlandRuntimeAsync(instanceId, cancellationToken);
                        continue;
                    }
                    WonderlandSnapshot? run;
                    GameSessionContext[] members;
                    lock (_gate)
                    {
                        if (!entry.Value.Sealed) continue;
                        run = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
                        {
                            var current = map.AdvanceWonderland(now);
                            if (current is null) return null;
                            if (_wonderlandDepartures.ContainsKey(instanceId) && map.Population == 0 &&
                                !_sessions.Values.Any(member => member.WorldInstanceId == instanceId))
                                current = map.CancelWonderland(now);
                            _ = map.TrySpawnPendingWonderlandStage(now, out var published);
                            return published ?? current;
                        });
                        members = SnapshotWonderlandMembersLocked(runtime);
                        if (run is not null)
                        {
                            // A run whose registered leader has left keeps an
                            // ending authority: the earliest present participant
                            // takes the leadership over.
                            MaintainWonderlandLeaderLocked(
                                instanceId,
                                run,
                                members);
                        }
                        _wonderlandDepartures.TryRemove(instanceId, out _);
                    }
                    if (run is null) continue;
                    await PublishWonderlandRunUiAsync(runtime, run, entry.Value, members, now, cancellationToken);
                    if (run.State == WonderlandRunState.Active) continue;
                    CaptureWonderlandTerminationNotice(runtime, run, entry.Value, members);
                    if (HasPendingWonderlandTitles(instanceId)) continue;
                    // The leave countdown a completed run shows, and the same one
                    // an ended or timed-out run shows, hold the members inside
                    // until it expires; they are carried out afterwards.
                    if (WonderlandCompletionPolicy.IsTreasureWindowOpen(run, now) ||
                        WonderlandCompletionPolicy.IsEndWindowOpen(run, now)) continue;
                    await ExitWonderlandMembersAsync(runtime, entry.Value, run, members, cancellationToken);
                    _wonderlandRetirements.TryAdd(instanceId, 0);
                    await TryRetireWonderlandRuntimeAsync(instanceId, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
                {
                    LogWonderlandDeferred(instanceId, now, error);
                }
            }
            await PublishExitedWonderlandNoticesAsync();
        }
        finally { Volatile.Write(ref _wonderlandWorldInFlight, 0); }
    }

    internal bool TryTerminateWonderlandRun(ClientSession session, int? repetitionId,
        int repetitionIndex, DateTimeOffset now)
    {
        if (repetitionId is not null and not WonderlandClientSceneId || repetitionIndex != 0) return false;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var actor) ||
                !IsCurrentWonderlandMember(actor, actor.WorldInstanceId) ||
                !_wonderlandAdmissions.TryGetValue(actor.WorldInstanceId, out var admission) ||
                // Only the instance's registered leader may end the run. Party
                // leadership belongs to a separate system and must not gate this:
                // ANDing them locked the control out for every member whenever the
                // instance leader was not the party leader.
                actor.CharacterId != admission.LeaderId ||
                !WorldInstances.TryFind(actor.WorldInstanceId, out var runtime)) return false;
            var terminated = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
            {
                if (!map.TryGetWonderlandSnapshot(out var current) || current.State != WonderlandRunState.Active)
                    return null;
                return map.CancelWonderland(now);
            });
            if (terminated?.State != WonderlandRunState.Cancelled) return false;
            CaptureWonderlandTerminationNoticeLocked(runtime, terminated, admission,
                SnapshotWonderlandMembersLocked(runtime));
            return true;
        }
    }

    private Task? RecordCommittedWonderlandDepartureLocked(GameSessionContext? previous)
    {
        if (previous is null || previous.MapId != WonderlandMapId) return null;
        _sessions.TryGetValue(previous.Session, out var current);
        if (current?.WorldInstanceId == previous.WorldInstanceId) return null;
        _wonderlandDepartures.TryAdd(previous.WorldInstanceId, 0);
        _wonderlandUi.TryRemove(previous.Session, out _);
        ClearWonderlandPlayerEffects(previous.Session);
        // The native list clear is not sent here: the shared end-of-run flow
        // sends the one 10231 with zero seconds when a member leaves any of the
        // four runs.
        return null;
    }

    private async Task ExitWonderlandMembersAsync(WorldInstanceRuntime runtime,
        WonderlandAdmission admission, WonderlandSnapshot run,
        IReadOnlyList<GameSessionContext> members, CancellationToken cancellationToken)
    {
        // The one automatic exit every dungeon shares: the countdown expired, so
        // whoever is still inside is carried home.
        var transferred = await EgressInstanceRunEndAsync(
            BeginInstanceRunEnd(runtime.InstanceId, InstanceRunKind.Wonderland,
                "Wonderland", WonderlandMapId, WonderlandClientSceneId,
                admission.DailyLimit, run.CompletedIslands,
                run.TerminalAt ?? DateTimeOffset.UtcNow,
                window: run.State == WonderlandRunState.Completed
                    ? WonderlandCompletionPolicy.TreasureWindow
                    : WonderlandCompletionPolicy.EndWindow),
            [.. members.Select(ToInstanceRunEndMember)],
            TransitionPartyMemberToAuthoritativeInstanceAsync,
            cancellationToken);
        if (transferred)
        {
            Console.WriteLine($"[wonderland] exit instance={runtime.InstanceId} " +
                $"reason={run.State} terminal={run.TerminalAt:O}");
        }
    }

    private async Task TryRetireWonderlandRuntimeAsync(WorldInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        if (!_wonderlandRetirements.ContainsKey(instanceId) || HasPendingWonderlandTitles(instanceId)) return;
        for (var phase = 0; phase < 3; phase++)
        {
            if (!WorldInstances.TryFind(instanceId, out var runtime))
            {
                ForgetWonderlandRun(instanceId);
                return;
            }
            var descriptor = runtime.Descriptor;
            bool empty;
            if (descriptor.LifecycleState == WorldInstanceLifecycleState.Closed)
                empty = runtime.Map.Population == 0;
            else
            {
                if (runtime.Owner.GetSnapshot().State != SingleOwnerMailboxState.Accepting) return;
                empty = InvokeWorldOwner(runtime, map => map.Population == 0 &&
                    map.TryGetWonderlandSnapshot(out var run) && run.State != WonderlandRunState.Active);
            }
            if (!empty) return;
            var at = Maximum(DateTimeOffset.UtcNow, descriptor.LastTransitionAt);
            var result = descriptor.LifecycleState switch
            {
                WorldInstanceLifecycleState.Active => await WorldInstances.BeginDrainAsync(instanceId,
                    descriptor.Revision, at, cancellationToken),
                WorldInstanceLifecycleState.Draining => await WorldInstances.CloseAsync(instanceId,
                    descriptor.Revision, at, cancellationToken),
                WorldInstanceLifecycleState.Closed => await WorldInstances.RemoveClosedAsync(instanceId, cancellationToken),
                _ => default
            };
            if (result.Status is WorldInstanceRuntimeDirectoryStatus.Removed or WorldInstanceRuntimeDirectoryStatus.InstanceNotFound)
            {
                ForgetWonderlandRun(instanceId);
                return;
            }
            if (result.Status is not (WorldInstanceRuntimeDirectoryStatus.Draining or WorldInstanceRuntimeDirectoryStatus.Closed)) return;
        }
    }

    private void ForgetWonderlandRun(WorldInstanceId instanceId)
    {
        if (HasPendingWonderlandTitles(instanceId)) return;
        if (_wonderlandAdmissions.TryRemove(instanceId, out var admission))
            _wonderlandReservations.TryRemove(admission.ReservationId, out _);
        ForgetInstanceRunEnd(instanceId);
        ForgetInstanceRunMembership(instanceId);
        _wonderlandDepartures.TryRemove(instanceId, out _);
        _wonderlandRetirements.TryRemove(instanceId, out _);
        _wonderlandLastError.TryRemove(instanceId, out _);
        foreach (var pair in _wonderlandUi.Where(pair => pair.Value.InstanceId == instanceId))
            _wonderlandUi.TryRemove(pair);
        ForgetSettledWonderlandTitles(instanceId);
        ForgetWonderlandCompletionNotice(instanceId);
        ForgetWonderlandCombat(instanceId);
        ForgetWonderlandBossLoot(instanceId);
    }

    private void LogWonderlandDeferred(WorldInstanceId instanceId, DateTimeOffset now, Exception error)
    {
        if (_wonderlandLastError.TryAdd(instanceId, now) ||
            _wonderlandLastError.TryGetValue(instanceId, out var previous) &&
            now - previous >= TimeSpan.FromSeconds(30) && _wonderlandLastError.TryUpdate(instanceId, now, previous))
            Console.Error.WriteLine($"[wonderland] deferred instance={instanceId}: {error.Message}");
    }
}
