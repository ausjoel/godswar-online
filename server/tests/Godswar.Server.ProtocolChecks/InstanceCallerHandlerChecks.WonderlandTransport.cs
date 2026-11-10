using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandTransportCheckName =
        "Wonderland captured transporters are visible and require their native dialogue actions";

    public static async Task RunWonderlandTransportAsync()
    {
        CheckWonderlandTransportRoster();
        await CheckWonderlandTransportVisibilityAndActionsAsync();
        await CheckWonderlandTransportDialogAuthorityAsync();
    }

    private static void CheckWonderlandTransportRoster()
    {
        var npcs = WonderlandTraversalPolicy.AddTeleporters([]);
        Check.True(npcs.Count == 9 && npcs.Select(npc => npc.ObjectId).Distinct().Count() == 9 &&
            npcs.Select(npc => npc.InteractionId).Distinct().Count() == 9,
            "all eight island transporters and the entrance Blackmarket Teleporter are published exactly once");
        foreach (var (id, key, x, z, facingBits) in new[]
        {
            (5205u, "Fane_001", 153f, -125f, 0x4044999A),
            (5206u, "Fane_002", -5f, -168.5f, 0x4014999A),
            (5221u, "Fane_017", 165f, -219f, 0x3FD9999A)
        })
        {
            var npc = npcs.Single(value => value.ObjectId == id);
            var packet = PacketBuilder.NpcSpawns([npc]);
            Check.True(npc.InteractionId == id && npc.NpcKey == key && npc.TemplateKey == $"{key}_Male15" &&
                npc.X == x && npc.Z == z && BitConverter.SingleToInt32Bits(npc.Facing) == facingBits &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == 0x00CF0211 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == id &&
                BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(28)) == x &&
                BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(36)) == z,
                $"{key} uses independently captured identity/coordinates/facing in the local compatible NPC packet");
        }
        var retained = new NpcSpawnDefinition(207, "Fane", "Other", "Other_Male15", 7999, 0, 0,
            7999, 0x211, 0, [], []);
        var stale = WonderlandTransportProtocol.CapturedFirstTeleporter with { ObjectId = 5700, InteractionId = 5700 };
        var replaced = WonderlandTraversalPolicy.AddTeleporters([retained, stale, WonderlandTransportProtocol.Blackmarket]);
        Check.True(replaced.Count == 10 && ReferenceEquals(replaced.Single(npc => npc.NpcKey == "Other"), retained) &&
            replaced.All(npc => npc.ObjectId != 5700),
            "legacy synthetic transporters are replaced without removing unrelated NPCs");
        var firstGeometry = WonderlandTerrainPolicy.GetIsland(1);
        Check.True(firstGeometry.Entrance.X == 169 && firstGeometry.Entrance.Z == -216 &&
            firstGeometry.Exit.X == 153 && firstGeometry.Exit.Z == -125,
            "the recovery entrance matches the instance caller landing while retaining the captured onward-transporter safe zone");
    }

    private static async Task CheckWonderlandTransportVisibilityAndActionsAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var leader = fixture.Party.Leader;
        Check.True(HasWonderlandNpcAppearance(leader.ReadPackets(), 5221, 165f, -219f),
            "the entrance Blackmarket NPC is sent during actual map readiness at the player's starting location");
        var before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5221));
        var ack = leader.ReadPackets().Skip(before).Single();
        Check.True(ack.AsSpan(0, 24).SequenceEqual(Convert.FromHexString(
                "3000532765140000000200002B40C20346616E655F303137")) &&
            ack.AsSpan(24).IndexOfAnyExcept((byte)0) < 0,
            "the entrance advertises captured59/62/63 with Fane_017 and initializes the unused ACK padding");
        var store = InstallBlackmarketHandlerStore(leader);
        var originalMap = leader.Character.CurrentMap;
        var originalMoney = leader.Character.Silver;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5221, 59));
        Check.True(leader.Character.CurrentMap == originalMap && leader.Character.Silver == originalMoney - 5000 &&
            store.Charges.Single().TargetIsland == 1 &&
            leader.Character.PositionX == WonderlandTerrainPolicy.GetIsland(1).Entrance.X &&
            leader.Character.PositionZ == WonderlandTerrainPolicy.GetIsland(1).Entrance.Z,
            "the entrance recovery service works before defeating Alpha without advancing island progress");
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);

        before = leader.ReadPackets().Count;
        await MoveToWonderlandTransportAsync(leader, 5205, 153f, -125f);
        Check.True(HasWonderlandNpcAppearance(leader.ReadPackets().Skip(before), 5205, 153f, -125f),
            "the first island's transporter enters the actual visibility stream at its captured location");
        before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5205));
        var opened = leader.ReadPackets().Skip(before).Single();
        Check.True(opened.AsSpan(0, 24).SequenceEqual(Convert.FromHexString(
                "3000532755140000000200003900000046616E655F303031")) &&
            GetSourceInstanceId(leader) == runtime.InstanceId && leader.Character.PositionX == 153f,
            "10067 opens the captured Teleport dialogue and never immediately moves the character");
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5205, 57));
        Check.True(leader.ReadPackets().Last().SequenceEqual(
            PacketBuilder.CapturedNpcFunctionActionResponse(5205, 57, [0, 100])),
            "locked island-one Teleport uses the native numbered-isle explanation instead of a custom server note");
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        await MoveToWonderlandTransportAsync(leader, 5205, 153f, -125f);
        before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5205));
        Check.True(leader.ReadPackets().Skip(before).All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "even an unlocked NPC waits for the Teleport button");
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5205, 57));
        var target = WonderlandTerrainPolicy.GetIsland(2).Entrance;
        Check.True(leader.ReadPackets().Skip(before).Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1 &&
            leader.Character.PositionX == target.X && leader.Character.PositionZ == target.Z &&
            GetSourceInstanceId(leader) == runtime.InstanceId,
            "57/-1 moves to the protected next-island arrival in the same admitted dungeon");
    }

    private static bool HasWonderlandNpcAppearance(IEnumerable<byte[]> packets, uint id, float x, float z) =>
        packets.Any(packet => packet.Length >= 104 && ReadOpcode(packet) == 10020 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == id &&
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(28)) == x &&
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(36)) == z);

    private static GamePacket CreateWonderlandTransportClick(uint npcId)
    {
        var bytes = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 48);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.NpcDialogOpen);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), npcId);
        return new(bytes);
    }

    private static GamePacket CreateWonderlandTransportAction(uint npcId, int dialogIndex,
        int subId = -1, int? repeatedDialog = null)
    {
        var bytes = Enumerable.Repeat((byte)0xA5, 92).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), dialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), repeatedDialog ?? dialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        return new(bytes);
    }

    private static async Task MoveToWonderlandTransportAsync(InstanceCallerFixture actor, uint npcId, float x, float z)
    {
        actor.Character.PositionX = x;
        actor.Character.PositionZ = z;
        actor.Registry.UpdateCharacter(actor.Session, actor.Character, advanceWorldRevision: false);
        await (Task)FindHandlerMethod("RefreshNearbyWorldObjectsAsync").Invoke(actor.Handler,
            [$"WonderlandTransportTest:{npcId}", CancellationToken.None])!;
    }
}
