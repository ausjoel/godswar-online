using System.Buffers.Binary;

namespace Godswar.Server.State;

/// <summary>One &lt;LvGain&gt; row of the client's Timeprize.xml &lt;LostPrize&gt;: what one hour away pays at that level.</summary>
internal readonly record struct OfflinePrizeGain(int Level, decimal Experience, decimal TalentPoints, decimal Energy,
    decimal BindingGold);

/// <summary>The amounts one offline prize pays.</summary>
internal readonly record struct OfflinePrizeAmounts(long Experience, long TalentPoints, long BindingGold, long Energy)
{
    public bool IsEmpty => Experience <= 0 && TalentPoints <= 0 && BindingGold <= 0 && Energy <= 0;
}

/// <summary>
/// Offline prize (the admin F-131, 2026-11-08: "Build it: per hour away"). The client's Timeprize.xml &lt;LostPrize&gt; gates
/// it at level 60 and 1440 minutes away; each &lt;LvGain&gt; row is what one whole hour away pays at that level.
/// Wire (client decompile, GodsWar-Native/decomp/out part0006 case 0x2855 MSG_OFFLINE_PRIZE -> FUN_006304f0):
///   10325 server -> client, 20 bytes, push only (the client never asks; it reads it only in game state 4, the world):
///   u32 +4, u32 +8, u32 +0xC, u32 +0x10. The client prints Message.dat TIME_PRIZE_CODE20 ("Over 24h to your last
///   logout. You've gained ") + the +4 value, then for each later field that is not zero its label (CODE21 " Exp,",
///   CODE22 " TP, ", CODE23 "B-Gold") + that value, then CODE24 " Stored Engergy.", in a system message box.
///   Read as EXP, Talent Points, B-Gold, Stored Energy (the order of the LvGain columns). UNPROVEN on a real client:
///   the label of a field is printed only when the NEXT field is not zero, so the exact sentence needs a rig look.
/// </summary>
internal static partial class TimePrizeCatalog
{
    public const ushort OfflinePrizeOpcode = 10325;
    public const int OfflinePrizePacketSize = 20;

    /// <summary>The LvGain row for a level; above the client's last row (140) the last row is used (GENERATED).</summary>
    public static OfflinePrizeGain? OfflineGainFor(int level)
    {
        if (level < OfflineMinLevel || OfflineGains.Count == 0) return null;
        var last = OfflineGains[^1];
        if (level >= last.Level) return last;
        var index = level - OfflineGains[0].Level;
        return index >= 0 && index < OfflineGains.Count ? OfflineGains[index] : null;
    }

    /// <summary>
    /// What a character of <paramref name="level"/> earns for being away <paramref name="away"/>: each LvGain value
    /// times the whole hours away, EXP and TP times their rate, each total floored once (so 0.504 TP at x10 is 5 TP,
    /// not 0). Nothing below the level or the 24-hour gate. No cap (the admin chose per hour with no cap; the per-minute
    /// option was the capped one).
    /// </summary>
    public static OfflinePrizeAmounts OfflineAmounts(int level, TimeSpan away, decimal experienceRate = 1m,
        decimal talentRate = 1m)
    {
        if (away.TotalMinutes < OfflineMinMinutes || OfflineGainFor(level) is not { } gain) return default;
        var hours = (decimal)Math.Floor(away.TotalHours);
        static long Total(decimal perHour, decimal hours, decimal rate) =>
            (long)Math.Min(decimal.Floor(perHour * hours * Math.Max(0m, rate)), int.MaxValue);
        return new OfflinePrizeAmounts(Total(gain.Experience, hours, experienceRate),
            Total(gain.TalentPoints, hours, talentRate), Total(gain.BindingGold, hours, 1m),
            Total(gain.Energy, hours, 1m));
    }

    public static byte[] OfflinePrize(int experience, int talentPoints, int bindingGold, int energy)
    {
        var packet = new byte[OfflinePrizePacketSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, OfflinePrizePacketSize);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), OfflinePrizeOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)Math.Max(0, experience));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)Math.Max(0, talentPoints));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)Math.Max(0, bindingGold));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), (uint)Math.Max(0, energy));
        return packet;
    }
}
