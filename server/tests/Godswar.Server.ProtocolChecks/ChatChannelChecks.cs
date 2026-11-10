using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The chat channels: the channel word the stock client writes, and the audience
/// each channel reaches.
/// </summary>
/// <remarks>
/// Every channel word asserted here is a frame the operator sent on the local
/// server on 2026-10-01, one message per chat window channel; the private pair came
/// from the two private messages he sent. The delivery half is asserted against the
/// registry's own recipient resolution, because that is where "which map is the
/// recipient on" stops mattering.
/// </remarks>
internal static class ChatChannelChecks
{
    public const string CheckName =
        "Chat channels: world, faction, guild, party and private audiences";

    private const byte AthensCamp = 0;
    private const byte SpartaCamp = 1;
    private const byte SharedMapId = GameDefaults.AthensCapitalMap;

    private static readonly DateTimeOffset TestTime =
        new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        CheckChannelWords();
        CheckPrivateTextSplit();
        await CheckAudiencesAsync();
        CheckOutgoingFrameShape();
    }

    /// <summary>
    /// The captured channel words, and the audience each one resolves to.
    /// </summary>
    private static void CheckChannelWords()
    {
        CheckAudience(0x410, ChatAudience.Nearby, "0x410 is the nearby channel");
        CheckAudience(0x402, ChatAudience.Party, "0x402 is the party channel");
        CheckAudience(0x404, ChatAudience.Guild, "0x404 is the guild channel");
        CheckAudience(0x408, ChatAudience.Faction, "0x408 is the faction channel");
        CheckAudience(
            0x482,
            ChatAudience.World,
            "0x482 is the world channel, the word both world sends carried");
        CheckAudience(
            0x40401,
            ChatAudience.Private,
            "0x40401 is a private message to 12e1");
        CheckAudience(
            0x60401,
            ChatAudience.Private,
            "0x60401 is a private message to 12v312: the extra bit the two " +
            "captured private frames differ by does not change the audience");
        CheckAudience(
            0x12345678,
            ChatAudience.Nearby,
            "an unknown channel word falls back to the sender's own map " +
            "instead of being dropped or sent wider");
        CheckAudience(
            0x40000,
            ChatAudience.Nearby,
            "the private flag without the private selector is not a private " +
            "message this build knows, so it stays on the sender's own map");
    }

    private static void CheckAudience(
        uint channelWord,
        ChatAudience expected,
        string description) =>
        Check.True(
            ChatChannelProtocol.ResolveAudience(channelWord) == expected,
            description);

    /// <summary>
    /// The captured private text: the client writes the recipient's name in front of
    /// the speaker's own, then a colon and the message.
    /// </summary>
    private static void CheckPrivateTextSplit()
    {
        Check.True(
            ChatChannelProtocol.TryReadPrivateRecipient(
                "12e1test:nihao",
                "test",
                out var recipient,
                out var message) &&
            recipient == "12e1" &&
            message == "nihao",
            "the captured private text yields its recipient and message");
        Check.True(
            ChatChannelProtocol.TryReadPrivateRecipient(
                "12v312test:haha",
                "test",
                out var secondRecipient,
                out var secondMessage) &&
            secondRecipient == "12v312" &&
            secondMessage == "haha",
            "the second captured private text yields its recipient and message");
        Check.True(
            ChatChannelProtocol.TryReadPrivateRecipient(
                "test:hello",
                "test",
                out _,
                out _) == false,
            "a public line carrying the speaker's own name is not a private " +
            "message");
        Check.True(
            ChatChannelProtocol.TryReadPrivateRecipient(
                "12e1someoneelse:nihao",
                "test",
                out _,
                out _) == false,
            "a name pair that does not end with the speaker's name is refused");

        // The client's private line is "[text 0..4] + 对 + [text 4..8] + 说: + [text
        // 9..]". Two live measurements from 2026-10-01 pin it: a body of
        // "test:nihao" drew nothing after the colon, and a body of
        // "test12e1说:hello" drew "test对12e1说:说:hello" - so the client writes its
        // own 说: and the colon at character eight is the one it replaces. The body
        // is therefore "<lead><visible>:<message>".
        var recipientCopy = ChatChannelProtocol.BuildPrivateBody(
            "test",
            "12e1",
            "hello");
        var senderCopy = ChatChannelProtocol.BuildPrivateBody(
            "12e1",
            "test",
            "hello");
        Check.True(
            recipientCopy == "test12e1:hello" &&
            senderCopy == "12e1test:hello" &&
            recipientCopy.Substring(4, 4) == "12e1" &&
            recipientCopy[9..] == "hello" &&
            senderCopy.Substring(4, 4) == "test" &&
            senderCopy[9..] == "hello" &&
            recipientCopy[8] == ':' &&
            !recipientCopy.Contains('说') &&
            !senderCopy.Contains('说'),
            "each side's copy puts the other party's name in the slot the " +
            "client renders and never writes 说, which the client adds itself");

        // A name shorter than the four-character slot is padded and a longer one
        // truncated, so the offsets hold for every name the operator can have.
        var shortName = ChatChannelProtocol.BuildPrivateBody(
            "ab",
            "cd",
            "hello");
        var longName = ChatChannelProtocol.BuildPrivateBody(
            "12v312",
            "tester",
            "hello");
        Check.True(
            shortName.Substring(4, 4) == "cd  " &&
            shortName[9..] == "hello" &&
            longName.Substring(4, 4) == "test" &&
            longName[9..] == "hello",
            "the name slots are exact: a short name is padded and a long one " +
            "truncated, so the message always starts at character nine");
    }

    /// <summary>
    /// One captured frame, byte for byte, read the way the server reads it.
    /// </summary>
    private static void CheckOutgoingFrameShape()
    {
        // test → 12e1, "nihao": a frame with the exact captured contents (length
        // 36, opcode 10035, the captured channel word and the captured text),
        // read from its payload as the server reads it.
        var captured = PacketBuilder.ChatMessage(
            0x1448,
            0x40401,
            "12e1test:nihao");
        Check.True(
            ChatChannelProtocol.TryRead(
                captured.AsSpan(4),
                out var channelWord,
                out var text) &&
            channelWord == 0x40401 &&
            text == "12e1test:nihao",
            "the captured private frame reads back its channel word and text");

        var rebuilt = PacketBuilder.ChatMessage(0x1448, 0x60401, "test:nihao");
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(rebuilt) == rebuilt.Length &&
            BinaryPrimitives.ReadUInt16LittleEndian(rebuilt.AsSpan(2)) ==
                Opcodes.Talk &&
            BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(4)) ==
                0x1448 &&
            BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(8)) ==
                (uint)System.Text.Encoding.Unicode.GetByteCount("test:nihao") + 2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(12)) ==
                0x60401 &&
            ChatChannelProtocol.TryRead(
                rebuilt.AsSpan(4),
                out var rebuiltChannel,
                out var rebuiltText) &&
            rebuiltChannel == 0x60401 &&
            rebuiltText == "test:nihao",
            "an outgoing chat frame carries the opcode, the speaker's object " +
            "id, the channel word, the text's byte count and its UTF-16 text");
    }

    /// <summary>
    /// One sender and two other characters on the same map: the world channel
    /// reaches both, the faction channel reaches only the one in the sender's camp,
    /// and a named-character audience reaches exactly the names it was given.
    /// </summary>
    /// <remarks>
    /// Every character sits in one world instance here, which is deliberate: this
    /// asserts the recipient <em>selection</em>, and the delivery itself is the
    /// same session write that already reaches another map (the realm-wide
    /// announcement path). The channel audience never consults a map.
    /// </remarks>
    private static async Task CheckAudiencesAsync()
    {
        await using var senderSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var campmateSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var foreignerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = new GameSessionRegistry();

        var sender = CreateCharacter(101, senderSocket.Session, "ChannelSender",
            SpartaCamp);
        var campmate = CreateCharacter(102, campmateSocket.Session,
            "ChannelCampmate", SpartaCamp);
        var foreigner = CreateCharacter(103, foreignerSocket.Session,
            "ChannelForeigner", AthensCamp);
        registry.JoinMap(
            senderSocket.Session,
            sender.AccountId,
            sender,
            objectId: 900_101);
        registry.JoinMap(
            campmateSocket.Session,
            campmate.AccountId,
            campmate,
            objectId: 900_102);
        registry.JoinMap(
            foreignerSocket.Session,
            foreigner.AccountId,
            foreigner,
            objectId: 900_103);

        // Joining a shared map announces every character to the others, so the
        // sockets already hold world traffic. Drain it: what this check is about is
        // what the channels write.
        await DrainAsync(senderSocket);
        await DrainAsync(campmateSocket);
        await DrainAsync(foreignerSocket);

        var camp = registry.GetOnlineCharacterCamp(senderSocket.Session);
        Check.True(
            camp == SpartaCamp,
            "the faction channel reads the sender's own camp");

        var everyone = registry.ResolveChatRecipients(
            senderSocket.Session,
            camp: null,
            characterIds: null);
        Check.True(
            everyone.Length == 3 &&
            everyone.Count(recipient =>
                ReferenceEquals(recipient.Session, senderSocket.Session)) == 1,
            "the world channel reaches every online character, wherever they " +
            "are, and includes the sender so his own line reaches his history");

        var nearby = registry.ResolveChatRecipients(
            senderSocket.Session,
            camp: null,
            characterIds: null,
            nearby: true);
        Check.True(
            nearby.Length == 2 &&
            nearby.All(recipient =>
                !ReferenceEquals(recipient.Session, senderSocket.Session)),
            "the nearby channel keeps its own behaviour: the map broadcast " +
            "reaches the sender, so the channel list must not repeat him");

        var sameCamp = registry.ResolveChatRecipients(
            senderSocket.Session,
            camp,
            characterIds: null);
        Check.True(
            sameCamp.Length == 2 &&
            sameCamp.Any(recipient =>
                ReferenceEquals(recipient.Session, campmateSocket.Session)) &&
            sameCamp.Any(recipient =>
                ReferenceEquals(recipient.Session, senderSocket.Session)) &&
            sameCamp.All(recipient => recipient.SenderObjectId == 900_101) &&
            !sameCamp.Any(recipient =>
                ReferenceEquals(recipient.Session, foreignerSocket.Session)),
            "the faction channel reaches the sender's own camp, including " +
            "the sender himself, and no one from the other camp");

        var namedOnly = registry.ResolveChatRecipients(
            senderSocket.Session,
            camp: null,
            characterIds: [foreigner.Id]);
        Check.True(
            namedOnly.Length == 2 &&
            namedOnly.Any(recipient =>
                ReferenceEquals(recipient.Session, foreignerSocket.Session)) &&
            namedOnly.Any(recipient =>
                ReferenceEquals(recipient.Session, senderSocket.Session)) &&
            !namedOnly.Any(recipient =>
                ReferenceEquals(recipient.Session, campmateSocket.Session)),
            "a character-named audience (guild, party) reaches exactly those " +
            "characters plus the sender's own line, and ignores camp");

        var delivered = await registry.DeliverChatAsync(
            sameCamp,
            recipient => PacketBuilder.ChatMessage(
                recipient.SenderObjectId,
                0x408,
                "ChannelSender:hello"),
            "ChatFaction",
            CancellationToken.None);
        Check.Equal(2, delivered, "every faction recipient is written to");

        var received = await campmateSocket.ReadPacketAsync();
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(received.AsSpan(4)) ==
                900_101 &&
            BinaryPrimitives.ReadUInt32LittleEndian(received.AsSpan(12)) ==
                0x408 &&
            ChatChannelProtocol.TryRead(
                received.AsSpan(4),
                out _,
                out var receivedText) &&
            receivedText == "ChannelSender:hello",
            "the delivered frame carries the speaker's object id, the channel " +
            "word and the text");
        var ownEcho = await senderSocket.ReadPacketAsync();
        Check.True(
            ChatChannelProtocol.TryRead(
                ownEcho.AsSpan(4),
                out _,
                out var echoedText) &&
            echoedText == "ChannelSender:hello",
            "the sender receives his own line back, so it reaches his chat " +
            "history");

        Check.True(
            registry.TryFindOnlineChatTarget(
                sender.RealmId,
                "ChannelCampmate",
                senderSocket.Session,
                out var found,
                out var foundName) &&
            ReferenceEquals(found, campmateSocket.Session) &&
            foundName == "ChannelCampmate" &&
            !registry.TryFindOnlineChatTarget(
                sender.RealmId,
                "ChannelSender",
                senderSocket.Session,
                out _,
                out _) &&
            !registry.TryFindOnlineChatTarget(
                sender.RealmId,
                "NoSuchCharacter",
                senderSocket.Session,
                out _,
                out _),
            "a private message resolves its recipient's session, name and object " +
            "id by name, and refuses the sender himself or a name that is not online");
    }

    /// <summary>
    /// Reads and discards whatever world traffic a socket is already holding, so a
    /// later assertion is about that write alone.
    /// </summary>
    private static async Task DrainAsync(RuntimePolicySessionSocket socket)
    {
        while (socket.Available >= sizeof(ushort))
        {
            await socket.ReadPacketAsync();
        }
    }

    private static GameCharacter CreateCharacter(
        int characterId,
        Godswar.Server.Networking.ClientSession session,
        string name,
        byte camp) =>
        new()
        {
            Id = characterId,
            AccountId = characterId,
            Name = name,
            CreatedUtc = TestTime.UtcDateTime,
            Camp = camp,
            CurrentMap = SharedMapId,
            PositionX = 5f,
            PositionZ = 6f,
            Level = 60,
            CurrentHp = 5_000,
            MaxHp = 5_000,
            CurrentMp = 2_000,
            MaxMp = 2_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };
}
