using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task PublishAtlantisTerminationEgressAsync(
        AtlantisRunDelivery delivery, CancellationToken cancellationToken)
    {
        var instanceId = delivery.Runtime.InstanceId;
        // A completion exit must pass durable reward settlement, including a
        // leader's manual Finish/Leave request during the completion countdown.
        // The end-window expiry alone is the trigger: the leader's own click took
        // only the leader out through the shared clicker-only leave. It is
        // deliberately not gated on the leader having asked to end the run, so a
        // run that merely ran out of time still brings its members home when the
        // countdown expires.
        if (delivery.Run.State is AtlantisRunState.Active or AtlantisRunState.Completed ||
            !_atlantisTerminationEgressInFlight.TryAdd(instanceId, 0))
        {
            return;
        }
        try
        {
            // The one automatic exit every dungeon shares.
            var transferred = await EgressInstanceRunEndAsync(
                BeginInstanceRunEnd(instanceId, InstanceRunKind.Atlantis, "Atlantis",
                    DynamicDungeonContentMapPolicy.AtlantisPortalMapId,
                    AtlantisClientSceneId, delivery.DailyEntryLimit,
                    delivery.Run.TeamPoints,
                    delivery.Run.TerminalAt ?? DateTimeOffset.UtcNow),
                [.. delivery.Members.Select(static member => new InstanceRunEndMember(
                    member.Session, member.CharacterId, member.Ownership, member.Camp))],
                TransitionPartyMemberToAuthoritativeInstanceAsync,
                cancellationToken);
            if (!transferred)
            {
                Console.WriteLine(
                    $"[atlantis] termination exit will retry instance={instanceId}");
            }
            // Keep the request until retirement: a member who is still loading
            // or whose fenced transfer failed must exit on a later world tick.
        }
        finally
        {
            _atlantisTerminationEgressInFlight.TryRemove(instanceId, out _);
        }
    }
}
