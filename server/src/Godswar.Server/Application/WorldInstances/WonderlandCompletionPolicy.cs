namespace Godswar.Server.Application.WorldInstances;

internal static class WonderlandCompletionPolicy
{
    // Includes walking between final-island boss groups and collecting unlocked
    // treasure. Earlier island portals stay usable until the same deadline.
    public static readonly TimeSpan TreasureWindow = TimeSpan.FromMinutes(5);

    // A run the leader ended early, or one that ran out of time, gives its
    // members the same leave countdown a completed run shows.
    public static readonly TimeSpan EndWindow = TimeSpan.FromSeconds(30);

    public static bool IsTreasureWindowOpen(WonderlandSnapshot run, DateTimeOffset now) =>
        run.State == WonderlandRunState.Completed && run.TerminalAt is { } completed &&
        now >= completed && now - completed < TreasureWindow;

    /// <summary>
    /// Whether an ended (cancelled) or timed-out run is still inside its
    /// thirty-second leave countdown. Members are carried out when it expires.
    /// </summary>
    public static bool IsEndWindowOpen(WonderlandSnapshot run, DateTimeOffset now) =>
        run.State is WonderlandRunState.Cancelled or WonderlandRunState.TimedOut &&
        run.TerminalAt is { } terminal && now >= terminal &&
        now - terminal < EndWindow;

    public static bool AllowsIslandTravel(WonderlandSnapshot run, DateTimeOffset now) =>
        run.State == WonderlandRunState.Active && now < run.Deadline || IsTreasureWindowOpen(run, now);

    public static bool IsCombatWindowOpen(WonderlandSnapshot run, DateTimeOffset now) =>
        run.State == WonderlandRunState.Active && now >= run.StartedAt && now < run.Deadline ||
        IsTreasureWindowOpen(run, now);
}
