using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // Terminal deliveries and committed last-player departures enqueue work.
    // Empty admission runtimes are never treated as abandoned runs.
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _pendingAtlantisRetirements = [];
    private readonly ConcurrentDictionary<WorldInstanceId, DateTimeOffset>
        _atlantisRetirementLastLog = [];

    private async Task RetireFinishedEmptyAtlantisRunAsync(
        AtlantisRunDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.Run.State is not
            (AtlantisRunState.Completed or AtlantisRunState.TimedOut or AtlantisRunState.Cancelled))
        {
            return;
        }
        if (_atlantisCompletionRewards is not null &&
            !_atlantisCompletionRewardSettled.ContainsKey(delivery.Runtime.InstanceId))
        {
            // Removing the runtime discards the only copy of the run result, so a
            // terminal run whose durable reward has not settled yet is held open.
            // Every tick retries the settlement and logs a failure to retry.
            return;
        }

        _pendingAtlantisRetirements.TryAdd(delivery.Runtime.InstanceId, 0);
        await TryFinishAtlantisRetirementAsync(
            delivery.Runtime.InstanceId, cancellationToken);
    }

    private async Task RetryPendingAtlantisRetirementsAsync(
        CancellationToken cancellationToken)
    {
        foreach (var instanceId in _pendingAtlantisRetirements.Keys)
        {
            await TryFinishAtlantisRetirementAsync(instanceId, cancellationToken);
        }
    }

    private async Task TryFinishAtlantisRetirementAsync(
        WorldInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        if (!_pendingAtlantisRetirements.TryUpdate(instanceId, 1, 0))
        {
            return;
        }

        try
        {
            // At most Active -> Draining -> Closed -> Removed in one pass.
            // Every phase reloads the current descriptor and revision.
            for (var phase = 0; phase < 3; phase++)
            {
                if (!WorldInstances.TryFind(instanceId, out var runtime))
                {
                    CompleteAtlantisRetirement(instanceId);
                    return;
                }
                if (!AtlantisEncounterPolicy.IsAtlantisInstance(runtime.Descriptor) ||
                    !CanRetireFinishedAtlantisRuntime(runtime))
                {
                    return;
                }

                var descriptor = runtime.Descriptor;
                var at = Maximum(DateTimeOffset.UtcNow, descriptor.LastTransitionAt);
                var result = descriptor.LifecycleState switch
                {
                    WorldInstanceLifecycleState.Active =>
                        await WorldInstances.BeginDrainAsync(instanceId,
                            descriptor.Revision, at, cancellationToken),
                    WorldInstanceLifecycleState.Draining =>
                        await WorldInstances.CloseAsync(instanceId,
                            descriptor.Revision, at, cancellationToken),
                    WorldInstanceLifecycleState.Closed =>
                        await WorldInstances.RemoveClosedAsync(instanceId,
                            cancellationToken),
                    _ => default
                };
                if (result.Status is WorldInstanceRuntimeDirectoryStatus.Removed or
                    WorldInstanceRuntimeDirectoryStatus.InstanceNotFound)
                {
                    CompleteAtlantisRetirement(instanceId);
                    return;
                }
                if (result.Status is not
                    (WorldInstanceRuntimeDirectoryStatus.Draining or
                     WorldInstanceRuntimeDirectoryStatus.Closed))
                {
                    // Revision and population contention retry on the next
                    // tick; the directory performs its own final empty check.
                    return;
                }
            }
        }
        catch (Exception error) when (
            error is IOException or InvalidOperationException or TimeoutException or
                SingleOwnerMailboxAdmissionException or SingleOwnerMailboxStoppedException or
                SingleOwnerMailboxWorkerException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var observedAt = DateTimeOffset.UtcNow;
            if (!_atlantisRetirementLastLog.TryGetValue(instanceId, out var lastLog) ||
                observedAt - lastLog >= TimeSpan.FromSeconds(30))
            {
                _atlantisRetirementLastLog[instanceId] = observedAt;
                Console.WriteLine($"[atlantis] retirement deferred instance={instanceId} " +
                    $"reason={error.GetType().Name}");
            }
        }
        finally
        {
            _pendingAtlantisRetirements.TryUpdate(instanceId, 0, 1);
        }
    }

    private bool CanRetireFinishedAtlantisRuntime(WorldInstanceRuntime runtime)
    {
        if (_atlantisCompletionRewards is not null &&
            runtime.Map.TryGetAtlantisRunSnapshot(out var run) &&
            run.State == AtlantisRunState.Completed &&
            !_atlantisCompletionRewardSettled.ContainsKey(runtime.InstanceId))
        {
            return false;
        }
        if (runtime.Descriptor.LifecycleState == WorldInstanceLifecycleState.Closed)
        {
            // Close already drained the owner and fenced new admissions.
            // A queued read would be rejected by this stopped owner.
            return IsFinishedEmptyAtlantisMap(runtime.Map);
        }
        if (runtime.Owner.GetSnapshot().State != SingleOwnerMailboxState.Accepting)
        {
            return false;
        }
        return InvokeWorldOwner(runtime, IsFinishedEmptyAtlantisMap);
    }

    private static bool IsFinishedEmptyAtlantisMap(MapInstance map) =>
        map.Population == 0 && map.TryGetAtlantisRunSnapshot(out var run) &&
        run.State is AtlantisRunState.Completed or AtlantisRunState.TimedOut or AtlantisRunState.Cancelled;

    private void CompleteAtlantisRetirement(WorldInstanceId instanceId)
    {
        ForgetAtlantisRun(instanceId);
        _atlantisDepartures.TryRemove(instanceId, out _);
        _atlantisRetirementLastLog.TryRemove(instanceId, out _);
        _pendingAtlantisRetirements.TryRemove(instanceId, out _);
    }
}
