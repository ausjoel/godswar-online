namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal static TimeSpan GetRuntimeStatusExpiryDelay(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var remaining = expiresAt - now;
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero :
            remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
    }
}
