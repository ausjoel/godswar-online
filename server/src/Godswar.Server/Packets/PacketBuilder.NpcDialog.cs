using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] AthensNpc(GameCharacter character)
    {
        var stream = ValidPacketStreamPrefix(ReferencePackets.AthensNpc);
        return PatchReferencePlayerPackets(stream, character);
    }

    public static byte[] CityNpcFallback(GameCharacter character)
    {
        if (character.CurrentMap is not (0 or 1))
        {
            return [];
        }

        var packet = AthensNpc(character);
        if (character.CurrentMap == 0)
        {
            ReplaceAscii(packet, AthensTemplatePrefix, SpartaTemplatePrefix);
        }

        return OpenNpcsToAllFactions(packet);
    }

    public static byte[] CapturedNpcSpawns(IReadOnlyList<CapturedNpcSpawn> spawns)
    {
        if (spawns.Count == 0)
        {
            return [];
        }

        var length = 0;
        foreach (var spawn in spawns)
        {
            length += spawn.Packet.Length;
            length += spawn.Detail10077.Length;
            length += spawn.Detail10080.Length;
        }

        var stream = new byte[length];
        var offset = 0;
        foreach (var spawn in spawns)
        {
            spawn.Packet.CopyTo(stream.AsSpan(offset));
            offset += spawn.Packet.Length;
            spawn.Detail10077.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10077.Length;
            spawn.Detail10080.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10080.Length;
        }

        return OpenNpcsToAllFactions(stream);
    }

    public static byte[] NpcSpawns(IReadOnlyList<NpcSpawnDefinition> spawns)
    {
        if (spawns.Count == 0)
        {
            return [];
        }

        var length = 0;
        foreach (var spawn in spawns)
        {
            length = checked(
                length +
                WorldObjectAppearanceLength +
                spawn.Detail10077.Length +
                spawn.Detail10080.Length);
        }

        var stream = new byte[length];
        var offset = 0;
        foreach (var spawn in spawns)
        {
            WriteNpcWorldObjectAppearance(
                stream.AsSpan(offset, WorldObjectAppearanceLength),
                spawn);
            offset += WorldObjectAppearanceLength;
            spawn.Detail10077.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10077.Length;
            spawn.Detail10080.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10080.Length;
        }

        return OpenNpcsToAllFactions(stream);
    }

    /// <summary>
    /// W24 (the admin d148, 24 Nov: "any faction may talk to any NPC everywhere"). Bits 8-9 of the low word of a 10020
    /// world-object word are the NPC's camp (0x0011 Sparta, 0x0111 Athens, 0x0211 both); the stock client ignores a click
    /// on an NPC of the other camp and sends no 10067. With the hot switch npc.all_factions on (default), every NPC in the
    /// stream is sent as "both camps". Off: each NPC keeps its own stored or captured camp (the original game).
    /// Wonderland (map 207) is left alone: its island-5 treasure chests are per party camp on purpose.
    /// Captured streams are only changed on NPC words (low byte 0x11), never on anything else they may carry.
    /// </summary>
    internal static byte[] OpenNpcsToAllFactions(byte[] stream)
    {
        if (stream.Length == 0 || !Finalise2Rates.On(Finalise2RateKeys.NpcAllFactions))
        {
            return stream;
        }

        OpenNpcWordsToAllFactions(stream);
        return stream;
    }

    internal static int OpenNpcWordsToAllFactions(Span<byte> stream)
    {
        var changed = 0;
        var offset = 0;
        while (offset + 8 <= stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
            if (length < 4 || offset + length > stream.Length)
            {
                break;
            }

            if (length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(stream.Slice(offset + 2, 2)) == 0x2724)
            {
                var word = BinaryPrimitives.ReadUInt32LittleEndian(stream.Slice(offset + 4, 4));
                var opened = IsCampTransporterFrame(stream.Slice(offset, length)) ? word : AllFactionsNpcWord(word);
                if (opened != word)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(stream.Slice(offset + 4, 4), opened);
                    changed++;
                }
            }

            offset += length;
        }

        return changed;
    }

    /// <summary>
    /// W25 (the admin 24 Nov: "dont allow factions to use opposite factions transporters. Im sure the defaults were right"):
    /// transporters keep the camp they were published with, so the client refuses the other faction's transporter as in
    /// the original game. A transporter is any NPC whose name in the NPC text seed contains "Transporter" or "Teleporter".
    /// </summary>
    private static readonly HashSet<string> TransporterNpcKeys = Godswar.Server.State.NpcTemplateSeeds.Texts
        .Where(static t => t.DisplayName.Contains("Transporter", StringComparison.OrdinalIgnoreCase) ||
            t.DisplayName.Contains("Teleporter", StringComparison.OrdinalIgnoreCase))
        .Select(static t => t.NpcKey)
        .ToHashSet(StringComparer.Ordinal);

    internal static bool IsTransporterNpcKey(string npcKey) => TransporterNpcKeys.Contains(npcKey);

    private static bool IsCampTransporterFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < WorldObjectTemplateOffset + WorldObjectTemplateLength)
        {
            return false;
        }

        var raw = frame.Slice(WorldObjectTemplateOffset, WorldObjectTemplateLength);
        var end = raw.IndexOf((byte)0);
        var template = Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]);
        var cut = template.LastIndexOf('_');
        return cut > 0 && TransporterNpcKeys.Contains(template[..cut]);
    }

    /// <summary>W24: one 10020 word as npc.all_factions sends it (camp bits 8-9 = 2 on NPC words outside Wonderland).</summary>
    internal static uint AllFactionsNpcWord(uint word)
    {
        var mapId = (short)(word >> 16);
        var low = word & 0xFFFF;
        if (mapId == WonderlandTransportProtocol.MapId)
        {
            return word;
        }

        // W24 follow-up (d148): the generated Troy (44) and Parnassus (68) GM rows store 1 or 2, the template's SEX from
        // NpcTemplateSeed, not an appearance word, so they carry no NPC flags and no camp. Sent as 0x0211 like every other
        // GM NPC open to both camps. Pindus (38) keeps its per-camp Quartermasters and Altars until the admin decides.
        if (low is 1 or 2 && mapId != Godswar.Server.Application.WorldInstances.BattlefieldRewardPolicy.PindusMapId)
        {
            return (word & 0xFFFF0000u) | 0x0211u;
        }

        return (low & 0xFF) == 0x11 && ((low >> 8) & 0x3) != 2
            ? (word & ~0x0300u) | 0x0200u
            : word;
    }

    /// <summary>W24: the 10020 word the client receives now (the switch is hot).</summary>
    internal static uint SentNpcWord(uint word, string? npcKey = null) =>
        Finalise2Rates.On(Finalise2RateKeys.NpcAllFactions) && (npcKey is null || !IsTransporterNpcKey(npcKey))
            ? AllFactionsNpcWord(word)
            : word;

    public static int CountCityNpcSpawnPackets(ReadOnlySpan<byte> stream)
    {
        var count = 0;
        var offset = 0;
        while (offset + 4 <= stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
            if (length < 4 || offset + length > stream.Length)
            {
                break;
            }

            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(stream.Slice(offset + 2, 2));
            if (opcode == 0x2724)
            {
                count++;
            }

            offset += length;
        }

        return count;
    }

    public static byte[] NpcDialogOpenAck(uint npcId, int dialogIndex, string scriptKey)
    {
        return NpcDialogOpenAck(npcId, [dialogIndex], scriptKey);
    }

    /// <summary>
    /// Advertises ordered top-level extended NPC functions. The stock client
    /// decodes the field at offset 12 as base-1000 digits, starting with the
    /// least-significant digit, so 4 followed by 37 is encoded as 37004.
    /// </summary>
    public static byte[] NpcDialogOpenAck(
        uint npcId,
        IReadOnlyList<int> dialogIndices,
        string scriptKey)
    {
        return NpcDialogOpenAck(
            npcId,
            flags: 0x200,
            packedDialog: PackNpcDialogIndices(dialogIndices),
            scriptKey);
    }

    // The flags word is a bitmask of the pages an npc can open, not a single
    // choice. The reference server sent the combinations itself: 0x23 (bank 0x20
    // plus quest 3) for Sparta_100, the warehouse manager, and 0x07 (shop 4 plus
    // quest 3) for Sparta_096. So an npc that also has a quest adds
    // QuestContentBaseline.QuestOpenFlags through extraFlags instead of losing
    // whichever page it already had.

    public static byte[] NpcDescriptionDialogOpenAck(
        uint npcId,
        string scriptKey,
        int extraFlags = 0) =>
        NpcDialogOpenAck(
            npcId,
            flags: CapitalNpcServiceProtocol.DescriptionOpenFlags | extraFlags,
            packedDialog: 0,
            scriptKey);

    public static byte[] NpcShopDialogOpenAck(
        uint npcId,
        string scriptKey,
        int extraFlags = 0) =>
        NpcDialogOpenAck(
            npcId,
            flags: CapitalNpcServiceProtocol.ShopOpenFlags | extraFlags,
            packedDialog: 0,
            scriptKey);

    /// <summary>
    /// Opens a quest-giver NPC. The reference server advertised flags 3 with an
    /// empty packed-dialog field for the newbie guide, which is what makes the
    /// stock client show the quest page rather than a bare description.
    /// </summary>
    public static byte[] NpcQuestDialogOpenAck(
        uint npcId,
        string scriptKey,
        int extraFlags = 0) =>
        NpcDialogOpenAck(
            npcId,
            flags: QuestContentBaseline.QuestOpenFlags | extraFlags,
            packedDialog: 0,
            scriptKey);

    public static byte[] NpcPrizeChestDialogOpenAck(
        uint npcId,
        string scriptKey,
        int extraFlags = 0) =>
        NpcDialogOpenAck(
            npcId,
            flags: CapitalNpcServiceProtocol.PrizeChestOpenFlags | extraFlags,
            packedDialog: 0,
            scriptKey);

    /// <summary>
    /// Builds the 48-byte npc dialog acknowledgement from an explicit function
    /// type and packed button word. Used for the installed client's extended-npc
    /// function pages, where the flags word selects the client-side script.
    /// </summary>
    public static byte[] NpcFunctionDialogOpenAck(
        uint npcId,
        int functionType,
        int packedDialog,
        string scriptKey) =>
        NpcDialogOpenAck(npcId, functionType, packedDialog, scriptKey);

    private static byte[] NpcDialogOpenAck(
        uint npcId,
        int flags,
        int packedDialog,
        string scriptKey)
    {
        var packet = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), NpcDialogOpenOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), flags);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12, 4),
            packedDialog);
        PacketText.WriteFixedAscii(packet.AsSpan(16, 32), scriptKey);
        return packet;
    }

    internal static int PackNpcDialogIndices(
        IReadOnlyList<int> dialogIndices)
    {
        ArgumentNullException.ThrowIfNull(dialogIndices);
        if (dialogIndices.Count is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dialogIndices),
                "The stock client supports one to three packed NPC dialogs.");
        }

        var packed = 0;
        var multiplier = 1;
        var seen = new HashSet<int>();
        foreach (var dialogIndex in dialogIndices)
        {
            if (dialogIndex is < 1 or > 999)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dialogIndices),
                    "Each packed NPC dialog index must be between 1 and 999.");
            }

            if (!seen.Add(dialogIndex))
            {
                throw new ArgumentException(
                    "Packed NPC dialog indices must be unique.",
                    nameof(dialogIndices));
            }

            packed = checked(packed + (dialogIndex * multiplier));
            multiplier = checked(multiplier * 1000);
        }

        return packed;
    }

    /// <summary>
    /// Builds a private Talk reply using the identity and channel metadata from
    /// the command packet. The stock client includes the UTF-16 terminator in
    /// its length field but omits the terminator itself from the packet body.
    /// </summary>
    /// <summary>A talk frame carrying a GM window command, read like a typed one.</summary>
    public static byte[] GmPanelCommandFrame(int characterId, string command)
    {
        const int packetHeaderLength = 4;
        const int talkMetadataLength = 12;
        const uint personalChannel = 0x410;
        var text = Encoding.Unicode.GetBytes(command + "\0");
        var packet = new byte[packetHeaderLength + talkMetadataLength + text.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.Talk);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), characterId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), (uint)text.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), personalChannel);
        text.CopyTo(packet.AsSpan(packetHeaderLength + talkMetadataLength));
        return packet;
    }

    public static byte[] DeveloperCommandTalkReply(
        ReadOnlySpan<byte> requestPayload,
        string message)
    {
        const int talkMetadataLength = 12;
        const int talkTextLengthOffset = 4;
        const int talkChannelOffset = 8;
        const int packetHeaderLength = 4;
        if (requestPayload.Length < talkMetadataLength)
        {
            throw new ArgumentException("Talk request payload is incomplete.", nameof(requestPayload));
        }

        var text = Encoding.Unicode.GetBytes(message);
        var packetLength = checked(packetHeaderLength + talkMetadataLength + text.Length);
        if (packetLength > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(message), "Talk reply is too long.");
        }

        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.Talk);
        requestPayload[..sizeof(uint)].CopyTo(packet.AsSpan(packetHeaderLength, sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(packetHeaderLength + talkTextLengthOffset, sizeof(uint)),
            checked((uint)text.Length + sizeof(ushort)));
        requestPayload.Slice(talkChannelOffset, sizeof(uint)).CopyTo(
            packet.AsSpan(packetHeaderLength + talkChannelOffset, sizeof(uint)));
        text.CopyTo(packet.AsSpan(packetHeaderLength + talkMetadataLength));
        return packet;
    }

    public static byte[] NpcFunctionActionResponse(uint npcId, int dialogIndex, params int[] subIds)
    {
        var packet = new byte[12 + (subIds.Length * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), NpcFunctionActionResponseOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), dialogIndex);

        for (var i = 0; i < subIds.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12 + (i * 4), 4), subIds[i]);
        }

        Console.WriteLine(
            $"[10070-hex] npc={npcId} dialog={dialogIndex} " +
            $"subIds=[{string.Join(',', subIds)}] " +
            $"hex={Convert.ToHexString(packet)}");

        return packet;
    }

    public static byte[] CapturedNpcFunctionActionResponse(
        uint npcId,
        int dialogIndex,
        params int[] subIds)
    {
        if (subIds.Length > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subIds),
                "Captured NPC menus support at most twenty entries.");
        }

        var packet = new byte[96];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            NpcFunctionActionResponseOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), dialogIndex);
        for (var index = 0; index < subIds.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(12 + (index * sizeof(int))),
                subIds[index]);
        }
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(92), 6_496_257);
        return packet;
    }

    private static void WriteNpcWorldObjectAppearance(
        Span<byte> packet,
        NpcSpawnDefinition spawn)
    {
        packet.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(packet[..2], WorldObjectAppearanceLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(2, 2), 0x2724);
        var appearanceType = spawn.AppearanceType == 0
            ? NpcAppearanceDefaults.AppearanceType
            : spawn.AppearanceType;
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.Slice(4, 4),
            ComposeNpcObjectType(spawn.MapId, appearanceType));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8, 4), spawn.ObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(24, 4), 1521);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(28, 4), spawn.X);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(32, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(36, 4), spawn.Z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.Slice(40, 4),
            float.IsFinite(spawn.Facing)
                ? spawn.Facing
                : NpcAppearanceDefaults.Facing);
        PacketText.WriteFixedAscii(
            packet.Slice(WorldObjectTemplateOffset, WorldObjectTemplateLength),
            spawn.TemplateKey);
    }

    private static uint ComposeNpcObjectType(
        short mapId,
        uint appearanceType)
    {
        if (mapId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mapId),
                "An NPC appearance requires a nonnegative map ID.");
        }

        // Origin reads the high word as MapID and rejects the object before
        // creation when it differs from the local player's current map. The
        // low word retains the NPC appearance flags captured from the server.
        return ((uint)(ushort)mapId << 16) |
            (appearanceType & ushort.MaxValue);
    }
}
