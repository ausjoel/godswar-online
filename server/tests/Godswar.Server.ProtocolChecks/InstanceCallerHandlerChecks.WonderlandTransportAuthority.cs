using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckWonderlandTransportDialogAuthorityAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var leader = fixture.Party.Leader;
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        await MoveToWonderlandTransportAsync(leader, 5205, 153, -125);
        var before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5205, 57));
        Check.True(leader.ReadPackets().Count == before,
            "an unlocked transporter cannot act without its own preceding native dialog open");
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5205));
        before = leader.ReadPackets().Count;
        var badLength = CreateWonderlandTransportAction(5205, 57).Buffer;
        BinaryPrimitives.WriteUInt16LittleEndian(badLength, 91);
        foreach (var packet in new[] { new GamePacket(badLength),
            CreateWonderlandTransportAction(5205, 57, subId: 0),
            CreateWonderlandTransportAction(5205, 57, repeatedDialog: 58),
            CreateWonderlandTransportAction(5205, 59) })
            await InvokeAsync(leader.Handler, packet);
        Check.True(leader.ReadPackets().Count == before && leader.Character.PositionX == 153,
            "noncanonical length, sub-ID, duplicated function and unadvertised function fail closed");

        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(999999));
        before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5205, 57));
        Check.True(leader.ReadPackets().Count == before,
            "opening another NPC, including an unknown one, revokes previous transporter dialog authority");
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5205));
        var context = GetHandlerField<object>(leader.Handler, "_wonderlandTransportDialog")!;
        context.GetType().GetProperty("ExpiresAt")!.SetValue(context, DateTimeOffset.UtcNow.AddSeconds(-1));
        before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5205, 57));
        Check.True(leader.ReadPackets().Count == before, "expired transport dialog cannot move an unlocked character");

        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5205));
        leader.Registry.AdvancePlayerLifeRevision(leader.Session);
        before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5205, 57));
        Check.True(leader.ReadPackets().Count == before,
            "a new player life cannot reuse pre-death transporter dialog authority");

        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5205));
        leader.Character.PositionX += 9;
        leader.Registry.UpdateCharacter(leader.Session, leader.Character, advanceWorldRevision: false);
        before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5205, 57));
        Check.True(leader.ReadPackets().Count == before,
            "moving outside eight-unit interaction distance invalidates the pending Teleport action");

        await MoveToWonderlandTransportAsync(leader, 5205, 153, -125);
        await InvokeAsync(leader.Handler, CreateWonderlandTransportClick(5205));
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5205, 57));
        Check.True(GetHandlerField<object>(leader.Handler, "_wonderlandTransportDialog") is null,
            "accepted Teleport consumes its exact one-use dialog before scene transition");
        before = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandTransportAction(5205, 57));
        Check.True(leader.ReadPackets().Skip(before).All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "replaying the previous island's Teleport cannot schedule another scene transition");
    }
}
