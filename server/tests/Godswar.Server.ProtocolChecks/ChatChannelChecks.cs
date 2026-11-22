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
            "0x60401 is a private message to 12v312: the target name length " +
            "byte does not change the audience");
        // Measured 2026-11-12 with the real client: Odinii (6) whispering Bob (3)
        // wrote 0x30601, which the old flag test (bits 0x40000 and 0x400) took for
        // a nearby line and showed to the whole map.
        CheckAudience(
            0x30601,
            ChatAudience.Private,
            "0x30601 (a 6-letter name whispering a 3-letter one) is private");
        CheckAudience(
            0x0C0301,
            ChatAudience.Private,
            "0x0C0301 (a 3-letter name whispering a 12-letter one) is private");
        CheckAudience(
            0x100801,
            ChatAudience.Private,
            "0x100801 (an 8-letter name whispering a 16-letter one) is private");
        CheckAudience(
            0x0810,
            ChatAudience.Nearby,
            "0x810 is the nearby channel from an 8-letter name");
        CheckAudience(
            0x0582,
            ChatAudience.World,
            "0x582 is the world channel from a 5-letter name");
        CheckAudience(
            0x0302,
            ChatAudience.Party,
            "0x302 is the party channel from a 3-letter name");
        CheckAudience(
            0x50483,
            ChatAudience.Nearby,
            "0x83, the server's own 'Sent to' type, is not a channel a client " +
            "can address: it never becomes a private or world message");
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

        // The client cuts the text by the two name length bytes of the channel
        // word (GodsWar.exe 0x5351bf): target = text[0..aim], speaker =
        // text[aim..aim+res], message = the rest. Each case below is a frame the
        // real client 1.5.0 wrote or drew on the private rig on 2026-11-12.
        CheckSplit(0x50601, "FreyaOdinii:oi", "Odinii", "Freya", "oi",
            "Odinii whispering Freya (the frame the real client wrote)");
        CheckSplit(0x60501, "OdiniiFreya:well well", "Freya", "Odinii",
            "well well", "Freya whispering Odinii");
        CheckSplit(0x30601, "BobOdinii:secret", "Odinii", "Bob", "secret",
            "a 3-letter recipient");
        CheckSplit(0x0C0301, "Twelve12NameAbc:hi", "Abc", "Twelve12Name", "hi",
            "a 3-letter speaker and a 12-letter recipient with digits");
        // One name inside the other: only the lengths can place the cut.
        CheckSplit(0x70401, "OdinJoeOdin:hi", "Odin", "OdinJoe", "hi",
            "a recipient whose name starts with the speaker's name");
        CheckSplit(0x80401, "AnnaOdinOdin:hi", "Odin", "AnnaOdin", "hi",
            "a recipient whose name ends with the speaker's name");
        CheckSplit(0x40801, "OdinAnnaOdin:hi", "AnnaOdin", "Odin", "hi",
            "a speaker whose name ends with the recipient's name");
        CheckSplit(0x30401, "A:bOdin:hi", "Odin", "A:b", "hi",
            "a colon inside the recipient's name does not move the cut");
        CheckSplit(0x50401, "FreyaOdin:a:b: c", "Odin", "Freya", "a:b: c",
            "colons in the message stay in the message");
        CheckSplit(0x50401, "Freyaodin:hi", "Odin", "Freya", "hi",
            "the speaker segment is compared without case");
        // Length bytes that do not describe the text fall back to the first-colon
        // rule, and that rule still refuses what it refused before.
        CheckSplit(0x40401, "12v312test:haha", "test", "12v312", "haha",
            "a wrong target length byte falls back to the first colon");
        Check.True(
            !ChatChannelProtocol.TryReadPrivateRecipient(
                0x40B01,
                "12e1someoneelse:nihao",
                "test",
                out _,
                out _) &&
            !ChatChannelProtocol.TryReadPrivateRecipient(
                0x00401,
                "test:hello",
                "test",
                out _,
                out _),
            "a frame whose speaker segment is not the session's character, or " +
            "that names no recipient, is refused");

        // Both sides get the same text; only the type byte differs. The lines in
        // the comments are what the real client drew (screens in
        // Development/w5/g-whisper/screens).
        CheckRelay("Freya", "Odin", "oi", "FreyaOdin: oi", 0x50401, 0x50483,
            "Odin Say to you: oi", " Sent to-> Freya: oi");
        CheckRelay("Odin", "Freya", "well well", "OdinFreya: well well",
            0x40501, 0x40583,
            "Freya Say to you: well well", " Sent to-> Odin: well well");
        CheckRelay("Twelve12Name", "Abc", "hi", "Twelve12NameAbc: hi",
            0x0C0301, 0x0C0383,
            "Abc Say to you: hi", " Sent to-> Twelve12Name: hi");
        CheckRelay("Abc", "Twelve12Name", "hi", "AbcTwelve12Name: hi",
            0x030C01, 0x030C83,
            "Twelve12Name Say to you: hi", " Sent to-> Abc: hi");
        CheckRelay("SixteenLetters16", "Fourteen14Name", "a:b",
            "SixteenLetters16Fourteen14Name: a:b", 0x100E01, 0x100E83,
            "Fourteen14Name Say to you: a:b",
            " Sent to-> SixteenLetters16: a:b");
        CheckRelay("X9", "Q", " spaced", "X9Q: spaced", 0x020101, 0x020183,
            "Q Say to you: spaced", " Sent to-> X9: spaced");

        Check.True(
            ChatChannelProtocol.BuildPrivateWord(
                ChatChannelProtocol.PrivateType,
                "Freya",
                "Odin",
                0x0705_0401) == 0x0705_0401 &&
            ChatChannelProtocol.BuildPrivateWord(
                ChatChannelProtocol.PrivateSentType,
                "Freya",
                "Odin",
                0x0709_0901) == 0x0705_0483,
            "the relayed word keeps the speaker's bubble style byte and takes " +
            "its length bytes from the real names, never from the client");
    }

    private static void CheckSplit(
        uint channelWord,
        string text,
        string speaker,
        string expectedRecipient,
        string expectedMessage,
        string description) =>
        Check.True(
            ChatChannelProtocol.TryReadPrivateRecipient(
                channelWord,
                text,
                speaker,
                out var recipient,
                out var message) &&
            recipient == expectedRecipient &&
            message == expectedMessage,
            "private text split: " + description);

    /// <summary>
    /// Builds both copies of one private message and reads them the way the
    /// client does: by the two length bytes of the channel word.
    /// </summary>
    private static void CheckRelay(
        string target,
        string speaker,
        string message,
        string expectedBody,
        uint expectedRecipientWord,
        uint expectedSenderWord,
        string expectedRecipientLine,
        string expectedSenderLine)
    {
        var body = ChatChannelProtocol.BuildPrivateBody(target, speaker, message);
        var recipientWord = ChatChannelProtocol.BuildPrivateWord(
            ChatChannelProtocol.PrivateType,
            target,
            speaker,
            0);
        var senderWord = ChatChannelProtocol.BuildPrivateWord(
            ChatChannelProtocol.PrivateSentType,
            target,
            speaker,
            0);
        Check.True(
            body == expectedBody &&
            recipientWord == expectedRecipientWord &&
            senderWord == expectedSenderWord &&
            ClientLine(recipientWord, body) == expectedRecipientLine &&
            ClientLine(senderWord, body) == expectedSenderLine,
            $"private relay {speaker} -> {target}: the recipient reads " +
            $"'{expectedRecipientLine}' and the sender '{expectedSenderLine}'");
    }

    /// <summary>
    /// The stock client's rule for a private line (GodsWar.exe 0x5351bf), with the
    /// en_us Message.dat texts SaytoU = " Say to you" and SentTo = " Sent to-> ".
    /// </summary>
    private static string ClientLine(uint channelWord, string text)
    {
        var aim = ChatChannelProtocol.ReadTargetNameLength(channelWord);
        var res = ChatChannelProtocol.ReadSpeakerNameLength(channelWord);
        var target = text[..aim];
        var speaker = text.Substring(aim, res);
        var message = text[(aim + res)..];
        return ChatChannelProtocol.ReadType(channelWord) ==
            ChatChannelProtocol.PrivateSentType
            ? " Sent to-> " + target + message
            : speaker + " Say to you" + message;
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
