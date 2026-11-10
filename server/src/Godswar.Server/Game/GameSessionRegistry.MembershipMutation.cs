using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // All work within this scope is synchronous. Acquire delivery ordering
    // first, then registry serialization, and revalidate the captured route.
    private MembershipMutation AcquireMembershipMutation(
        ClientSession session,
        WorldInstanceId? targetInstance = null,
        byte? targetMap = null)
    {
        while (true)
        {
            _sessions.TryGetValue(session, out var expected);
            MapInstance.PlayerRemovalLease? removal = null;
            var removesSource = expected is not null &&
                (targetInstance is { } instance
                    ? expected.WorldInstanceId != instance
                    : targetMap is { } map ? expected.MapId != map : true);
            if (removesSource && TryGetWorldInstance(expected!, out var runtime))
            {
                if (Monitor.IsEntered(_gate))
                {
                    throw new InvalidOperationException(
                        "Prepare membership removal before entering registry serialization.");
                }
                removal = runtime.Map.AcquirePlayerRemoval(session);
            }

            Monitor.Enter(_gate);
            _sessions.TryGetValue(session, out var current);
            if (ReferenceEquals(current, expected))
            {
                return new MembershipMutation(this, expected, removal);
            }
            Monitor.Exit(_gate);
            removal?.Dispose();
        }
    }

    private sealed class MembershipMutation(
        GameSessionRegistry registry,
        GameSessionContext? previous,
        MapInstance.PlayerRemovalLease? removal) : IDisposable
    {
        public MapInstance.PlayerRemovalLease? Removal { get; } = removal;

        public void Dispose()
        {
            Task? departureWrite = null;
            Task? wonderlandDepartureWrite = null;
            Task? instanceRunDepartureWrite = null;
            try
            {
                // The one clear of a finished run's native instance list, for
                // every dungeon, enqueued in the same fence as the departure.
                instanceRunDepartureWrite =
                    registry.RecordCommittedInstanceRunDepartureLocked(previous);
                departureWrite = registry.RecordCommittedAtlantisDepartureLocked(previous);
                // A Wonderland run is cancelled once its last member leaves, so
                // the departure has to be recorded inside this same membership
                // fence rather than inferred from the next world tick.
                wonderlandDepartureWrite = registry.RecordCommittedWonderlandDepartureLocked(previous);
            }
            finally
            {
                Monitor.Exit(registry._gate);
                Removal?.Dispose();
            }
            if (instanceRunDepartureWrite is not null)
            {
                _ = ObserveAtlantisDepartureWriteAsync(previous!.Session, instanceRunDepartureWrite);
            }
            if (departureWrite is not null)
            {
                _ = ObserveAtlantisDepartureWriteAsync(previous!.Session, departureWrite);
            }
            if (wonderlandDepartureWrite is not null)
            {
                _ = ObserveAtlantisDepartureWriteAsync(previous!.Session, wonderlandDepartureWrite);
            }
        }
    }
}
