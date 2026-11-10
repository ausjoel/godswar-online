using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal async ValueTask<bool>
        TryRetireEmptyLocalWorldInstanceAsync(
            WorldInstanceDescriptor createdDescriptor)
    {
        ArgumentNullException.ThrowIfNull(createdDescriptor);
        if (createdDescriptor.Kind != InstanceKind.Dungeon ||
            createdDescriptor.LifecycleState !=
                WorldInstanceLifecycleState.Active)
        {
            throw new ArgumentException(
                "Only an exact active dungeon descriptor can be retired " +
                "after a failed entry.",
                nameof(createdDescriptor));
        }

        var drainAt = Maximum(
            DateTimeOffset.UtcNow,
            createdDescriptor.LastTransitionAt);
        var drain = await WorldInstances.BeginDrainAsync(
            createdDescriptor.InstanceId,
            createdDescriptor.Revision,
            drainAt,
            CancellationToken.None);
        if (drain.Status ==
            WorldInstanceRuntimeDirectoryStatus.InstanceNotFound)
        {
            ForgetAtlantisRun(createdDescriptor.InstanceId);
            ForgetHarborAttackRun(createdDescriptor.InstanceId);
            ForgetTrainingGroundRun(createdDescriptor.InstanceId); // TRAINING
            return true;
        }
        if (drain.Status != WorldInstanceRuntimeDirectoryStatus.Draining ||
            drain.Runtime is null)
        {
            return false;
        }

        var drainingDescriptor = drain.Runtime.Descriptor;
        var closeAt = Maximum(
            DateTimeOffset.UtcNow,
            drainingDescriptor.LastTransitionAt);
        var close = await WorldInstances.CloseAsync(
            createdDescriptor.InstanceId,
            drainingDescriptor.Revision,
            closeAt,
            CancellationToken.None);
        if (close.Status ==
            WorldInstanceRuntimeDirectoryStatus.InstanceNotFound)
        {
            ForgetAtlantisRun(createdDescriptor.InstanceId);
            return true;
        }
        if (close.Status != WorldInstanceRuntimeDirectoryStatus.Closed)
        {
            return false;
        }

        var removal = await WorldInstances.RemoveClosedAsync(
            createdDescriptor.InstanceId,
            CancellationToken.None);
        var removed = removal.Status is
            WorldInstanceRuntimeDirectoryStatus.Removed or
            WorldInstanceRuntimeDirectoryStatus.InstanceNotFound;
        if (removed)
        {
            ForgetAtlantisRun(createdDescriptor.InstanceId);
            ForgetHarborAttackRun(createdDescriptor.InstanceId);
            ForgetTrainingGroundRun(createdDescriptor.InstanceId); // TRAINING
        }
        return removed;
    }

    private static DateTimeOffset Maximum(
        DateTimeOffset first,
        DateTimeOffset second) =>
        first >= second ? first : second;
}
