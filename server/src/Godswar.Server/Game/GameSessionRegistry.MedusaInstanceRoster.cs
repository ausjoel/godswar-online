using System.Collections.Concurrent;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<
        ClientSession,
        MedusaInstanceRosterStamp> _medusaInstanceRosterBySession = [];

    private IReadOnlyList<MedusaInstanceRosterDelivery>
        CaptureMedusaInstanceRosterDeliveries(
            WorldInstanceRuntime runtime,
            DateTimeOffset now)
    {
        if (!_medusaLeaderUi.ContainsKey(runtime.InstanceId))
        {
            return [];
        }

        var state = InvokeWorldOwner(
            runtime,
            static map =>
            {
                var isMedusa = map.TryGetMedusaOwnershipSnapshot(
                    out _);
                var members = isMedusa
                    ? map.Snapshot()
                        .Where(static context =>
                            context.WorldReady &&
                            !context.Session.IsDisconnected)
                        .OrderBy(static context => context.CharacterId)
                        .ToArray()
                    : [];
                return (isMedusa, members);
            });
        if (!state.isMedusa)
        {
            return [];
        }

        var memberSessions = state.members
            .Select(static member => member.Session)
            .ToHashSet();
        foreach (var cached in _medusaInstanceRosterBySession)
        {
            if (cached.Value.WorldInstanceId == runtime.InstanceId &&
                !memberSessions.Contains(cached.Key))
            {
                _medusaInstanceRosterBySession.TryRemove(cached);
            }
        }

        var roster = SnapshotInstanceRoster(
            runtime.InstanceId, InstanceRunKind.Medusa, now);
        if (roster.Length == 0)
        {
            // The native 10218 handler reads a first member even when the count is
            // zero, so an empty roster is never published; the run's own teardown
            // is what clears a panel.
            return [];
        }

        var signature = InstanceRosterSignature(roster);
        var stamp = new MedusaInstanceRosterStamp(
            runtime.InstanceId,
            signature);
        var packet = PacketBuilder.RepetitionInstanceMembers(roster);
        var deliveries = new List<MedusaInstanceRosterDelivery>(
            state.members.Length);
        foreach (var member in state.members)
        {
            if (_medusaInstanceRosterBySession.TryGetValue(
                    member.Session,
                    out var previous) &&
                previous == stamp)
            {
                continue;
            }

            _medusaInstanceRosterBySession[member.Session] = stamp;
            deliveries.Add(new(member.Session, packet, stamp));
        }

        return deliveries;
    }

    private async Task PublishMedusaInstanceRosterDeliveryAsync(
        MedusaInstanceRosterDelivery delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            await delivery.Session.SendAsync(
                delivery.Packet,
                cancellationToken,
                "MedusaInstanceMemberRoster");
        }
        catch (Exception error) when (
            error is IOException or ObjectDisposedException)
        {
            _medusaInstanceRosterBySession.TryRemove(
                new KeyValuePair<ClientSession, MedusaInstanceRosterStamp>(
                    delivery.Session,
                    delivery.Stamp));
            Remove(delivery.Session);
        }
    }

    private readonly record struct MedusaInstanceRosterStamp(
        WorldInstanceId WorldInstanceId,
        string Signature);

    private sealed record MedusaInstanceRosterDelivery(
        ClientSession Session,
        byte[] Packet,
        MedusaInstanceRosterStamp Stamp);
}
