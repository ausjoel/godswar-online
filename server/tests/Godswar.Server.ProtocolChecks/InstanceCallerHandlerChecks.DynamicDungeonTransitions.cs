using System.Reflection;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static readonly MethodInfo AuthoritativeInstanceTransitionMethod =
        FindHandlerMethod("HandleAuthoritativeInstanceTransitionAsync");

    private static async Task
        CheckAuthoritativeDynamicDungeonTransitionsAsync()
    {
        foreach (var targetMapId in new byte[] { 205, 207 })
        {
            await using var fixture = await CreateFixtureAsync(
                level: 90,
                transitionReady: true);
            var sourceInstanceId = GetSourceInstanceId(fixture);
            var created = await fixture.Registry.CreateLocalWorldInstanceAsync(
                RealmId.Tempest,
                new MapId(targetMapId),
                InstanceKind.Dungeon,
                playerCapacity: 3,
                CancellationToken.None);
            var target = created.Runtime ??
                throw new InvalidOperationException(
                    $"Dynamic dungeon {targetMapId} returned no runtime.");
            var ownership = PlayerOwnershipTestFences.ForCharacter(
                fixture.Character.Id);
            var command = new AuthoritativeInstanceTransitionCommand(
                fixture.Character.Id,
                sourceInstanceId,
                fixture.SourceMapId,
                ownership,
                target.InstanceId,
                targetMapId,
                TargetX: 11f,
                TargetZ: 12f);

            if (targetMapId ==
                DynamicDungeonContentMapPolicy.AtlantisPortalMapId)
            {
                var medusaCommand = new MedusaInstanceTransitionCommand(
                    command.CharacterId,
                    command.ExpectedSourceWorldInstanceId,
                    command.ExpectedSourceMapId,
                    command.ExpectedOwnership,
                    command.TargetWorldInstanceId,
                    command.TargetMapId,
                    command.TargetX,
                    command.TargetZ);
                Check.True(
                    !await InvokePartyTransitionAsync(
                        fixture.Handler,
                        medusaCommand,
                        CancellationToken.None) &&
                    GetSourceInstanceId(fixture) == sourceInstanceId &&
                    fixture.Character.CurrentMap == fixture.SourceMapId,
                    "Medusa-specific transition entry remains scoped to " +
                    "Medusa maps");
            }

            var packetCount = fixture.ReadPackets().Count;
            Check.True(
                await InvokeAuthoritativeTransitionAsync(
                    fixture.Handler,
                    command),
                $"authoritative exact-instance transition enters map {targetMapId}");
            var expectedScene = PacketBuilder.SceneChange(
                0x1448,
                command.TargetX,
                y: 0f,
                command.TargetZ,
                targetMapId);
            Check.True(
                fixture.Character.CurrentMap == targetMapId &&
                fixture.Registry.TryGetSessionWorldInstanceId(
                    fixture.Session,
                    out var currentInstanceId) &&
                currentInstanceId == target.InstanceId &&
                currentInstanceId != sourceInstanceId &&
                fixture.ReadPackets()
                    .Skip(packetCount)
                    .Any(packet => packet.SequenceEqual(expectedScene)),
                "dynamic-dungeon ingress uses the requested exact instance " +
                $"and native scene change for map {targetMapId}");
        }
    }

    private static async Task<bool> InvokeAuthoritativeTransitionAsync(
        GameClientHandler handler,
        AuthoritativeInstanceTransitionCommand command)
    {
        var task = AuthoritativeInstanceTransitionMethod.Invoke(
            handler,
            [command, CancellationToken.None]) as Task<bool> ??
            throw new InvalidOperationException(
                "Authoritative instance transition did not return a task.");
        return await task;
    }
}
