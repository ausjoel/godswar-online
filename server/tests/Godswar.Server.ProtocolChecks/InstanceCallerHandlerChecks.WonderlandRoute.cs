using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandRouteCheckName =
        "Wonderland clockwise admission, seven portal hops, and split-party revival returns to the first island";

    public static async Task RunWonderlandRouteAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 2);
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var leader = fixture.Party.Leader;
        var first = WonderlandTerrainPolicy.GetIsland(1);
        Check.True(leader.Character.PositionX == first.Entrance.X && leader.Character.PositionZ == first.Entrance.Z &&
            first.Entrance.X == 169 && first.Entrance.Z == -216 && first.Bounds.Contains(159, -163),
            "the native instance entry uses the user's exact southeast starting point");
        Check.True(first.Exit.X == 153 && first.Exit.Z == -125 &&
            !WonderlandTraversalPolicy.TryDetect(new(207, new(169, -216), new(170, -205)), out _) &&
            !WonderlandTraversalPolicy.TryDetect(new(207, new(170, -211), new(170, -203)), out _),
            "walking from the arrival or through the nearby first transporter cannot cause automatic travel");
        for (var island = 2; island <= 7; island++)
        {
            var exit = WonderlandTerrainPolicy.GetIsland(island).Exit;
            var automatic = WonderlandTraversalPolicy.TryDetect(new(207,
                new(exit.X - 1, exit.Z), new(exit.X + 1, exit.Z)), out var detected);
            Check.True(!automatic && detected == 0,
                "walking across any island's transporter area cannot bypass its native Teleport dialogue");
        }
        var secondChest = WonderlandTerrainPolicy.GetIsland(2).TreasureChest;
        Check.True(!WonderlandTraversalPolicy.TryDetect(new(207,
            new(secondChest.X - 1, secondChest.Z), new(secondChest.X + 1, secondChest.Z)), out _),
            "walking up to the captured island-two treasure cannot teleport the player before claiming it");
        var npcs = WonderlandTraversalPolicy.AddTeleporters([]);
        for (var island = 1; island <= 7; island++)
        {
            var source = WonderlandTerrainPolicy.GetIsland(island);
            var destination = WonderlandTerrainPolicy.GetIsland(island + 1);
            var npc = npcs.Single(value => value.ObjectId == WonderlandTraversalPolicy.FirstTeleporterObjectId + island - 1);
            var transporter = WonderlandTraversalPolicy.GetTeleporter(island);
            Check.True(npc.X == transporter.X && npc.Z == transporter.Z &&
                WonderlandTraversalPolicy.TryGetIsland(npc, out var npcIsland) && npcIsland == island,
                "the native teleporter uses its transport position independently of combat-safe-zone anchors");
            await ClearWonderlandHandlerIslandAsync(fixture, runtime);
            {
                leader.Character.PositionX = source.Center.X;
                leader.Character.PositionZ = source.Center.Z;
                leader.Character.CurrentHp = 0;
                leader.Registry.UpdateCharacter(leader.Session, leader.Character, advanceWorldRevision: false);
                var packetCount = leader.ReadPackets().Count;
                await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x1448));
                Check.True(leader.Character.PositionX == first.Entrance.X && leader.Character.PositionZ == first.Entrance.Z &&
                    leader.ReadPackets().Skip(packetCount).Any(packet => packet.SequenceEqual(
                        PacketBuilder.SceneChange(0x1448, first.Entrance.X, 0, first.Entrance.Z, 207))),
                    "revival from every outer island returns to island one without resetting the unlocked route");
                await CompleteAtlantisSceneReadinessAsync(leader.Handler);
            }
            var before = leader.ReadPackets().Count;
            await ClickWonderlandPortalAsync(leader, island);
            Check.True(GetSourceInstanceId(leader) == runtime.InstanceId &&
                leader.Character.PositionX == destination.Entrance.X && leader.Character.PositionZ == destination.Entrance.Z &&
                leader.ReadPackets().Skip(before).Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1,
                $"native portal {island} reaches the next clockwise island without changing dungeon identity");
            await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        }
        var follower = fixture.Party.Followers[0];
        follower.Character.CurrentHp = 0;
        Check.True(leader.Registry.TryReviveWonderlandPlayer(follower.Session, out var reviveInstance,
            out var revive, out _) && reviveInstance == runtime.InstanceId && revive == first.Entrance,
            "a member remaining on island one is not revived into the party leader's central eighth island");
    }
}
