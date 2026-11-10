namespace Godswar.Server.Protocol;

/// <summary>
/// Friend and blacklist frames, read from the Origin client: senders at 0x4b9cb0,
/// 0x4b9f20, 0x4b9e50, 0x4ba0c0, 0x4ba180, 0x4ba230 and handlers at 0x4ec4ec
/// (10098), 0x4ec885 (10099), 0x4ec99f (10100), 0x4eca5b (10102), 0x4ec827 (10103).
/// Offsets are from the start of the frame, including the 4 byte header.
/// </summary>
internal static class RelationProtocol
{
    public const byte KindFriend = 0;
    public const byte KindBlacklist = 1;

    /// <summary>The 2008 server's MAX_RELATION_NUM, per kind.</summary>
    public const int MaximumPerKind = 30;

    public const int RoleNameBytes = 32;

    /// <summary>
    /// The list entry holds 18 bytes and the client copies 10102's name into it with
    /// a checked format call (0x4ef570), which aborts on anything longer.
    /// </summary>
    public const int MaximumNameLength = 17;

    public const int ListHeaderBytes = 12;
    public const int ListEntryBytes = 36;
    public const int RequestBytes = 70;
    public const int ResponseBytes = 38;
    public const int AddedBytes = 48;
    public const int PresenceBytes = 12;

    public const sbyte RequestErrorNoSuchPlayer = -1;
    public const sbyte RequestErrorAlreadyListed = 0;
    public const sbyte RequestErrorListFull = 2;

    public static bool TryReadRequest(
        ReadOnlySpan<byte> payload,
        out string targetName,
        out byte kind)
    {
        targetName = string.Empty;
        kind = 0;
        if (payload.Length < RequestBytes - 4)
        {
            return false;
        }

        targetName = Packets.PacketText.ReadFixedAscii(
            payload,
            RoleNameBytes,
            RoleNameBytes);
        kind = payload[RoleNameBytes * 2];
        return targetName.Length > 0 && kind <= KindBlacklist;
    }

    public static bool TryReadNameAndFlag(
        ReadOnlySpan<byte> payload,
        out string name,
        out byte flag)
    {
        name = string.Empty;
        flag = 0;
        if (payload.Length < ResponseBytes - 4)
        {
            return false;
        }

        name = Packets.PacketText.ReadFixedAscii(payload, 0, RoleNameBytes);
        flag = payload[RoleNameBytes];
        return name.Length > 0;
    }
}
