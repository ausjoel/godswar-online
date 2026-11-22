using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.ProtocolChecks;

/// <summary>B21 item 29: the Online Award's "minutes online first" setting.</summary>
internal static class OnlineAwardOnlineTimeChecks
{
    public const string CheckName = "B21 Online Award minutes online first";

    public static Task RunAsync()
    {
        var start = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        Check.True(OnlineAwardOnlineTime.MinutesLeft(start, start, 0) == 0, "0 minutes = claim at once (stock)");
        Check.True(OnlineAwardOnlineTime.MinutesLeft(start, start, 30) == 30, "just entered: the full 30 minutes left");
        Check.True(OnlineAwardOnlineTime.MinutesLeft(start, start.AddSeconds(61), 30) == 29, "partial minutes round up");
        Check.True(OnlineAwardOnlineTime.MinutesLeft(start, start.AddMinutes(30), 30) == 0, "exactly on time may claim");
        Check.True(OnlineAwardOnlineTime.MinutesLeft(start, start.AddHours(5), 30) == 0, "long online may claim");
        Check.True(ServerRateCatalog.TryGet(OnlineAwardOnlineTime.Key, out var d), "the setting is a known rate");
        Check.True(d.DefaultValue == 0m && d.MaximumValue == 1440m, "default 0 (stock behaviour), at most a day");
        return Task.CompletedTask;
    }
}
