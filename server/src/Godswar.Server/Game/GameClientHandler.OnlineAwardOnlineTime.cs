using Godswar.Server.Application.OnlineAwards;

namespace Godswar.Server.Game;

// B21 item 29: the Online Award's "minutes online first" gate (OnlineAwardOnlineTime). The game connection opens when
// the character enters the world, so the handler's creation time is the start of this session.
internal sealed partial class GameClientHandler
{
    private readonly DateTimeOffset _onlineAwardSessionStart = DateTimeOffset.UtcNow; // B21

    /// <summary>True when the claim may go ahead; otherwise one chat line says how long is left (no pop-up).</summary>
    private async Task<bool> OnlineAwardOnlineTimeMetAsync(CancellationToken cancellationToken)
    {
        var left = OnlineAwardOnlineTime.MinutesLeft(
            _onlineAwardSessionStart, DateTimeOffset.UtcNow, OnlineAwardOnlineTime.RequiredMinutes());
        if (left <= 0) return true;
        await SendEventsNpcNoticeAsync(
            $"Stay online {left} more minute{(left == 1 ? "" : "s")} to collect the Online Award.",
            "OnlineAwardOnlineTime", cancellationToken);
        return false;
    }
}
