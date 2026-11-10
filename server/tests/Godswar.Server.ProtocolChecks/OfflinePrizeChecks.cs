using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>F-131 offline prize: the LostPrize gate, per-hour amounts and the 10325 layout.</summary>
internal static class OfflinePrizeChecks
{
    public const string CheckName = "F-131 offline prize: LostPrize gate, per hour away, 10325 layout";

    public static Task RunAsync()
    {
        Check.Equal(60, TimePrizeCatalog.OfflineMinLevel, "gate level from the client's lostPrize MinLv");
        Check.Equal(1440, TimePrizeCatalog.OfflineMinMinutes, "gate time from the client's lostPrize MinTime");
        Check.Equal(81, TimePrizeCatalog.OfflineGains.Count, "LvGain rows 60..140");

        var day = TimeSpan.FromHours(24);
        Check.Equal(new OfflinePrizeAmounts(720, 0, 0, 0), TimePrizeCatalog.OfflineAmounts(60, day),
            "Lv60, 24 h: 720 EXP; 0.504 TP and energy floor to 0");
        Check.Equal(new OfflinePrizeAmounts(1440, 1, 0, 1), TimePrizeCatalog.OfflineAmounts(60, TimeSpan.FromHours(48.9)),
            "Lv60, 48.9 h: whole hours only (48): 1440 EXP, 1 TP, 1 energy");
        Check.Equal(new OfflinePrizeAmounts(7200, 5, 0, 0), TimePrizeCatalog.OfflineAmounts(60, day, 10m, 10m),
            "Lv60, 24 h at x10: the rate scales before the floor (7200 EXP, 5 TP; energy unrated stays 0)");
        Check.True(TimePrizeCatalog.OfflineAmounts(60, TimeSpan.FromMinutes(1439)).IsEmpty, "under 24 h pays nothing");
        Check.True(TimePrizeCatalog.OfflineAmounts(59, TimeSpan.FromDays(30)).IsEmpty, "under level 60 pays nothing");
        Check.Equal(new OfflinePrizeAmounts(361 * 24, 2, 0, 2), TimePrizeCatalog.OfflineAmounts(140, day),
            "Lv140, 24 h: 8664 EXP, 2 TP (2.09), 2 energy");
        Check.Equal(TimePrizeCatalog.OfflineAmounts(140, day), TimePrizeCatalog.OfflineAmounts(200, day),
            "above the last row (140) the last row is used");
        Check.True(TimePrizeCatalog.OfflineAmounts(140, TimeSpan.FromDays(3650)).Experience == 361L * 24 * 3650,
            "no cap: ten years away pays ten years");

        var packet = TimePrizeCatalog.OfflinePrize(720, 3, 0, 5);
        Check.Equal(20, packet.Length, "10325 is 20 bytes");
        Check.Equal((ushort)20, BinaryPrimitives.ReadUInt16LittleEndian(packet), "size word");
        Check.Equal((ushort)10325, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)), "opcode");
        Check.Equal(720u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)), "+4 EXP");
        Check.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)), "+8 TP");
        Check.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)), "+0xC B-Gold");
        Check.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)), "+0x10 Stored Energy");
        return Task.CompletedTask;
    }
}
