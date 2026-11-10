namespace Godswar.Server.State;

internal readonly record struct TimePrizeItem(int ItemId, int Count, bool Bound);

internal readonly record struct TimePrizeTier(int Index, int Minutes, IReadOnlyList<TimePrizeItem> Items);

/// <summary>
/// W1 prize claims (2026-11-08): the client's online-time "beginner gift" window (AwardFunction.xml, CTimePrizeUI).
/// Wire (client decompile, GodsWar-Native/decomp/out part0006/0013):
///   10323 server -> client: u32 beginner online seconds, u32 weekly online seconds, u16 beginner gifts claimed,
///         u16 weekly gifts claimed. The client counts down to the next tier from its own copy of Timeprize.xml.
///   10326 client -> server (header only): claim the next beginner gift. Server -> client 10326 (header only) = claimed;
///         the client then advances its own counter and shows the tier's items.
///   10327: the weekly track. This client has no weekly table, so a claim is answered "all claimed".
///   10324 server -> client: u8 code 1 = not online long enough, 2 = all claimed, 3 = bag full.
/// The tiers come from the client's Timeprize.xml (TimePrizeCatalog.Generated.cs). The offline prize 10325 is in
/// OfflinePrizeCatalog.cs (the admin F-131: per hour away).
/// </summary>
internal static partial class TimePrizeCatalog
{
    public const ushort StatusOpcode = 10323;
    public const ushort ReturnCodeOpcode = 10324;
    public const ushort BeginnerClaimOpcode = 10326;
    public const ushort WeeklyClaimOpcode = 10327;
    public const byte NotYet = 1;
    public const byte AllClaimed = 2;
    public const byte BagFull = 3;
    /// <summary>Seconds of slack between the client's countdown and the server's 15-second accrual (GENERATED).</summary>
    public const int GraceSeconds = 30;
    public const int AccrualSeconds = 15;

    public static byte[] Status(int beginnerSeconds, int weeklySeconds, int beginnerClaimed, int weeklyClaimed)
    {
        var packet = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet, 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), StatusOpcode);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)Math.Max(0, beginnerSeconds));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)Math.Max(0, weeklySeconds));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12), (ushort)Math.Clamp(beginnerClaimed, 0, 255));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), (ushort)Math.Clamp(weeklyClaimed, 0, 255));
        return packet;
    }

    public static byte[] Claimed(ushort opcode)
    {
        var packet = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        return packet;
    }

    public static byte[] ReturnCode(byte code)
    {
        var packet = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet, 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), ReturnCodeOpcode);
        packet[4] = code;
        return packet;
    }
}
