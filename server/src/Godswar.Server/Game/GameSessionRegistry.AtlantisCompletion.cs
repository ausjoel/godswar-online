using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // The caller owns durable completion rewards. A failed or pending settlement
    // may publish the result UI, but cannot authorize a completion transfer.
    private async Task PublishAtlantisCompletionAsync(
        AtlantisRunDelivery delivery, bool rewardsSettled, CancellationToken cancellationToken)
    {
        if (delivery.Run.State != AtlantisRunState.Completed || delivery.Run.TerminalAt is null)
        {
            return;
        }
        // The one end-of-run flow every dungeon shares: the same four native
        // frames and the same thirty-second countdown, carrying Atlantis's own
        // score and its own scene. Atlantis keeps only its completion rule - the
        // carried members wait for the durable reward settlement.
        await PublishInstanceRunEndAsync(
            BeginInstanceRunEnd(delivery.Runtime.InstanceId, InstanceRunKind.Atlantis,
                "Atlantis", DynamicDungeonContentMapPolicy.AtlantisPortalMapId,
                AtlantisClientSceneId, delivery.DailyEntryLimit,
                delivery.Run.TeamPoints, delivery.Run.TerminalAt.Value),
            [.. delivery.Members.Select(static member => new InstanceRunEndMember(
                member.Session, member.CharacterId, member.Ownership, member.Camp))],
            delivery.ObservedAt,
            settle: null,
            cancellationToken);
        if (rewardsSettled)
        {
            await PublishAtlantisCompletionEgressAsync(delivery, cancellationToken);
        }
    }
}

