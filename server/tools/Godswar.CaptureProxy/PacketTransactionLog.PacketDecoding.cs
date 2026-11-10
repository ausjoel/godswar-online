using System.Buffers.Binary;
using System.Text;

sealed partial class PacketTransactionLog
{
    private const int SpawnPacketMinimumLength = 108;

    /// <summary>
    /// 识别 10020 刷怪包并取出 template_key。NPC 与怪物共用这个 opcode，靠 template_key 的命名区分。
    /// </summary>
    private static bool TryReadSpawnTemplate(
        PacketTransactionRecord packet,
        out string templateKey,
        out int length)
    {
        templateKey = string.Empty;
        length = 0;

        if (!string.Equals(packet.ConnectionName, "game", StringComparison.OrdinalIgnoreCase) ||
            packet.Direction != "S2C" ||
            packet.Opcode != 10020 ||
            packet.ClearBytes.Length < SpawnPacketMinimumLength)
        {
            return false;
        }

        length = BinaryPrimitives.ReadUInt16LittleEndian(packet.ClearBytes.AsSpan(0, 2));
        if (length > packet.ClearBytes.Length || length < SpawnPacketMinimumLength)
        {
            return false;
        }

        templateKey = ReadNullTerminatedAscii(packet.ClearBytes.AsSpan(44, length - 44));
        return !string.IsNullOrWhiteSpace(templateKey);
    }

    /// <summary>
    /// 解析 NPC 刷怪包的坐标。地图号不在这里决定，由 <see cref="NpcTemplateResolver"/> 在落库时解析。
    /// </summary>
    private static bool TryParseNpcSpawn(
        PacketTransactionRecord packet,
        string templateKey,
        int length,
        out CapturedNpcSpawnRecord spawn)
    {
        spawn = default;

        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(8, 4));
        var x = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(28, 4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(36, 4));
        if (objectId == 0 || IsReservedPlayerObjectId(objectId) || !float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        // npc_key 是"场景_序号"，与 npc_spawn_definitions 里的交互键一致（例如 Athens_006），
        // 不能只取场景名，否则同一地图的所有 NPC 会共用同一个键。
        // 调用方已用 IsNpcTemplate 校验过命名，这里去掉末尾的 _外观 段即可。
        var instanceSeparator = templateKey.LastIndexOf('_');
        spawn = new CapturedNpcSpawnRecord(
            templateKey[..instanceSeparator],
            templateKey,
            objectId,
            x,
            z,
            packet.ClearBytes[..length]);
        return true;
    }

    private static bool TryParseMonsterSpawn(
        PacketTransactionRecord packet,
        string templateKey,
        int length,
        out CapturedMonsterSpawnRecord spawn)
    {
        spawn = default;

        var objectType = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(4, 4));
        if ((objectType & 0xFFu) != 0x12u)
        {
            return false;
        }

        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(8, 4));
        var x = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(28, 4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(36, 4));
        if (objectId == 0 || IsReservedPlayerObjectId(objectId) || !float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        spawn = new CapturedMonsterSpawnRecord(
            templateKey,
            objectId,
            x,
            z,
            packet.ClearBytes[..length]);
        return true;
    }

    private static bool TryParseNpcDetailPacket(PacketTransactionRecord packet, out CapturedNpcDetailRecord detail)
    {
        detail = default;

        if (!string.Equals(packet.ConnectionName, "game", StringComparison.OrdinalIgnoreCase) ||
            packet.Direction != "S2C" ||
            packet.Opcode is not (10077 or 10080) ||
            packet.ClearBytes.Length < 8)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.ClearBytes.AsSpan(0, 2));
        if (length > packet.ClearBytes.Length || length < 8)
        {
            return false;
        }

        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(4, 4));
        detail = new CapturedNpcDetailRecord(packet.Opcode.Value, objectId, packet.ClearBytes[..length]);
        return true;
    }

    private static bool IsReservedPlayerObjectId(uint objectId)
    {
        return objectId == 0x1448 || objectId is >= 1 and <= 0x05DB;
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return Encoding.ASCII.GetString(bytes[..length]);
    }
}
