using System.Collections.Concurrent;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<WorldInstanceId, byte> _atlantisCompletionEgressInFlight = [];

    private void ForgetAtlantisCompletion(WorldInstanceId instanceId)
    {
        _atlantisCompletionEgressInFlight.TryRemove(instanceId, out _);
    }

    private async Task PublishAtlantisCompletionEgressAsync(
        AtlantisRunDelivery delivery, CancellationToken cancellationToken)
    {
        var instanceId = delivery.Runtime.InstanceId;
        if (delivery.Run.State != AtlantisRunState.Completed || delivery.Run.TerminalAt is not { } completedAt ||
            delivery.ObservedAt - completedAt < InstanceRunEndDelay ||
            !_atlantisCompletionEgressInFlight.TryAdd(instanceId, 0))
        {
            return;
        }
        try
        {
            // The one automatic exit every dungeon shares: the countdown expired,
            // so whoever is still inside is carried home. A member who pressed
            // leave during the countdown left on his own, through the shared
            // clicker-only leave.
            await EgressInstanceRunEndAsync(
                BeginInstanceRunEnd(instanceId, InstanceRunKind.Atlantis, "Atlantis",
                    DynamicDungeonContentMapPolicy.AtlantisPortalMapId,
                    AtlantisClientSceneId, delivery.DailyEntryLimit,
                    delivery.Run.TeamPoints, completedAt),
                [.. delivery.Members.Select(static member => new InstanceRunEndMember(
                    member.Session, member.CharacterId, member.Ownership, member.Camp))],
                TransitionPartyMemberToAuthoritativeInstanceAsync,
                cancellationToken);
            // A failed transfer retains membership and is retried from the next
            // world delivery; the committed departure clears native repetition
            // state through the shared end-of-run clear.
        }
        finally
        {
            _atlantisCompletionEgressInFlight.TryRemove(instanceId, out _);
        }
    }
}
