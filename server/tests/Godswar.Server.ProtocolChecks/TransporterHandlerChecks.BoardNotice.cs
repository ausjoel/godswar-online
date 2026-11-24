using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

// W20 (npc-form-notice, found live 22 Nov by B27-C): the board above the chat box is fed by 10070 frames on window
// 770079. The exe keeps ONE NPC-function context (npc id + dialog index) taken from the last 10070 it received, and
// Board.lua only stops the repaint, so a board line that arrived while an NPC form was open made the form's OK go out
// as npc=770079 dialog=770079 and the server ignored it. This check models that client: every OK is built from the
// last 10070 the session received, exactly as the exe does.
internal static partial class TransporterHandlerChecks
{
    public const string BoardNoticeCheckName =
        "W20 NPC form + board notice: the form's OK still works when the online line arrives meanwhile";

    public static async Task RunBoardNoticeAsync()
    {
        await CheckNoticeDuringOpenFormAsync();
        await CheckNoticeWithoutNpcGoesAtOnceAsync();
        await CheckHeldLinesKeepLatestAsync();
        await CheckNpcReplyInfoIsImmediateAsync();
    }

    // The answer to the player's own NPC click ("your bag is full", "You exchanged ...") must not wait for the hold:
    // while a form may be open it goes out at once as a 10038 game log line, which leaves the NPC context alone.
    private static async Task CheckNpcReplyInfoIsImmediateAsync()
    {
        await using var fixture = await CreateFixtureAsync(Sparta(), level: 1);
        fixture.Registry.MarkBoardCapable(fixture.Session);
        fixture.Registry.NoteNpcFormActivity(fixture.Session);
        var before = fixture.ReadPackets().Count;
        const string reply = "Growth reward: your bag is full.";
        Check.True(await fixture.Registry.SendServerInfoAsync(fixture.Session, reply, CancellationToken.None),
            "an NPC reply info line is delivered while the form is open");
        var sent = fixture.ReadPackets().Skip(before).ToArray();
        Check.True(sent is [var line] && line.SequenceEqual(PacketBuilder.PersonalGameLog(reply)),
            "it goes out at once as one game log line, not as a board frame");
        Check.Equal(0, await fixture.Registry.FlushHeldBoardAsync(
            DateTimeOffset.UtcNow + GameSessionRegistry.NpcFormHold, CancellationToken.None),
            "and it is not held for the board as well");
    }

    // No NPC traffic: the board line goes out at once, and a board declaration (a 10069 with the info-tab tag, not an
    // NPC window) does not start a hold.
    private static async Task CheckNoticeWithoutNpcGoesAtOnceAsync()
    {
        await using var fixture = await CreateFixtureAsync(Sparta(), level: 1);
        await InvokeAsync(fixture.Handler, CreateInfoTabsDeclaration(version: 2));
        var declared = fixture.ReadPackets();
        Check.True(declared.Any(IsBoardWindowFrame),
            "a board declaration gets its online line at once (the declaration is not NPC traffic)");
        var before = declared.Count;
        await fixture.Registry.PublishPopulationNoticeOnceAsync(CancellationToken.None);
        Check.True(fixture.ReadPackets().Skip(before).Any(IsBoardWindowFrame),
            "with no NPC form open the periodic online line reaches the board at once");
    }

    // Held lines: the latest online line and the last 3 messages, sent once the session has been quiet at NPCs for the
    // hold; nothing is sent before then, and nothing twice.
    private static async Task CheckHeldLinesKeepLatestAsync()
    {
        var endpoint = Sparta();
        await using var fixture = await CreateFixtureAsync(endpoint, level: 1);
        fixture.Registry.MarkBoardCapable(fixture.Session);
        var opened = DateTimeOffset.UtcNow;
        fixture.Registry.NoteNpcFormActivity(fixture.Session, opened);
        var before = fixture.ReadPackets().Count;
        await fixture.Registry.SendBoardToAsync(fixture.Session, 'O', "Online: 1", opened, CancellationToken.None);
        await fixture.Registry.SendBoardToAsync(fixture.Session, 'O', "Online: 2", opened, CancellationToken.None);
        for (var i = 1; i <= 5; i++)
            await fixture.Registry.SendBoardToAsync(fixture.Session, 'M', $"note {i}", opened, CancellationToken.None);
        Check.Equal(0, await fixture.Registry.FlushHeldBoardAsync(
            opened + GameSessionRegistry.NpcFormHold - TimeSpan.FromSeconds(1), CancellationToken.None),
            "held board lines wait while the NPC form may still be open");
        Check.True(!fixture.ReadPackets().Skip(before).Any(IsBoardWindowFrame), "nothing reached the board during the hold");
        Check.Equal(1, await fixture.Registry.FlushHeldBoardAsync(opened + GameSessionRegistry.NpcFormHold, CancellationToken.None),
            "held board lines go out once the session has been quiet at NPCs for the hold");
        var text = BoardTextOf(fixture.ReadPackets().Skip(before));
        Check.True(text.Contains("OOnline: 2", StringComparison.Ordinal) && !text.Contains("Online: 1", StringComparison.Ordinal),
            $"only the latest online line is sent ({text})");
        Check.True(!text.Contains("note 2", StringComparison.Ordinal) &&
            new[] { "note 3", "note 4", "note 5" }.All(n => text.Contains(n, StringComparison.Ordinal)),
            $"the last 3 messages are sent, oldest first ({text})");
        Check.True(text.IndexOf("note 3", StringComparison.Ordinal) < text.IndexOf("note 5", StringComparison.Ordinal),
            "held messages keep their order");
        Check.Equal(0, await fixture.Registry.FlushHeldBoardAsync(
            opened + GameSessionRegistry.NpcFormHold + TimeSpan.FromMinutes(1), CancellationToken.None),
            "held lines are sent once only");
    }

    // Decodes the board text frames the way Board_TextEntry does (control value, then 3 characters per value).
    private static string BoardTextOf(IEnumerable<byte[]> packets)
    {
        var text = new System.Text.StringBuilder();
        foreach (var packet in packets.Where(IsBoardWindowFrame))
        {
            var control = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12));
            var count = (control / 2) - ((control / 64) * 32);
            if (control / 64 == 1) text.Append('|');
            for (var i = 0; i < count; i++)
            {
                var value = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(16 + (i * 4)));
                foreach (var c in new[] { value >> 16, (value >> 8) & 0xFF, value & 0xFF })
                    if (c is >= 32 and < 127) text.Append((char)c);
            }
        }
        return text.ToString();
    }

    private static GamePacket CreateInfoTabsDeclaration(int version)
    {
        var bytes = new byte[TransporterProtocol.ActionPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, TransporterProtocol.ActionPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)((0x49 << 24) | (version << 16)));
        return new GamePacket(bytes);
    }

    private static async Task CheckNoticeDuringOpenFormAsync()
    {
        var endpoint = Sparta();
        await using var fixture = await CreateFixtureAsync(endpoint, level: 1);
        var destination = TransporterProtocol.Destinations.Single(
            static candidate => candidate.NpcKey == "Sparta_042" && candidate.SubId == 1);

        // The player opens the Transporter's form (the client now holds npc/dialog from the menu's 10070).
        await IssueTransporterMenuAsync(fixture, endpoint);
        fixture.Registry.MarkBoardCapable(fixture.Session); // a 1.4.3+ client with the board
        var beforeNotice = fixture.ReadPackets().Count;

        // The periodic "Online: ..." notice fires while the form is open.
        await fixture.Registry.PublishPopulationNoticeOnceAsync(CancellationToken.None);
        var noticePackets = fixture.ReadPackets().Skip(beforeNotice).ToArray();
        Check.True(
            !noticePackets.Any(IsBoardWindowFrame),
            "no board 10070 frame reaches a session whose NPC form is open (it would replace the form's NPC context)");

        // The player clicks OK: the client builds it from its last 10070, as the exe does.
        var context = LastNpcFunctionContext(fixture.ReadPackets());
        Check.True(
            context.NpcId == endpoint.InteractionId && context.DialogIndex == TransporterProtocol.DialogIndex,
            $"the client's NPC context still points at the Transporter (npc={context.NpcId} dialog={context.DialogIndex})");
        var beforeOk = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateClientModelAction(context, destination.SubId));
        var traversal = GameplayContentTestFixtures.Runtime.MapTraversal;
        Check.True(
            traversal.TryGetAutomaticLink(destination.ArrivalAnchorSourceMapId, destination.TargetMapId, out var anchor) &&
            traversal.TryResolveTargetArrival(anchor, PortalRadius, out var arrival) &&
            fixture.ReadPackets().Skip(beforeOk).Any(packet => packet.SequenceEqual(PacketBuilder.SceneChange(
                LocalPlayerObjectId,
                arrival.TargetArrival.X,
                y: 0f,
                arrival.TargetArrival.Z,
                checked((byte)destination.TargetMapId)))),
            "the OK clicked after the notice is honoured: the Transporter moves the player");
        Check.True(
            fixture.Store.PositionWrites.Count == 1 && fixture.Character.CurrentMap == destination.TargetMapId,
            "the action happens once, on the first click");

        // The online line is not lost: it reaches the board once the player has been quiet at NPCs for the hold.
        var beforeFlush = fixture.ReadPackets().Count;
        await fixture.Registry.FlushHeldBoardAsync(
            DateTimeOffset.UtcNow + GameSessionRegistry.NpcFormHold, CancellationToken.None);
        Check.True(
            BoardTextOf(fixture.ReadPackets().Skip(beforeFlush)).Contains("Online: 1", StringComparison.Ordinal),
            "the held online line reaches the board after the hold");
    }

    private static bool IsBoardWindowFrame(byte[] packet) =>
        packet.Length >= 12 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == Opcodes.NpcFunctionActionResponse &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == (uint)GameSessionRegistry.BoardWindowId;

    // The exe's NPC-function context: npc id and dialog index of the last 10070 the client received.
    private static (uint NpcId, int DialogIndex) LastNpcFunctionContext(IReadOnlyList<byte[]> packets)
    {
        var last = packets.Last(static packet =>
            packet.Length >= 12 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == Opcodes.NpcFunctionActionResponse);
        return (BinaryPrimitives.ReadUInt32LittleEndian(last.AsSpan(4)), BinaryPrimitives.ReadInt32LittleEndian(last.AsSpan(8)));
    }

    private static GamePacket CreateClientModelAction((uint NpcId, int DialogIndex) context, int subId)
    {
        var bytes = new byte[TransporterProtocol.ActionPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, TransporterProtocol.ActionPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), context.NpcId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), context.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), context.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        for (var index = 0; index < TransporterProtocol.FunctionArgumentCount; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + (index * sizeof(int))), -1);
        }
        return new GamePacket(bytes);
    }
}
