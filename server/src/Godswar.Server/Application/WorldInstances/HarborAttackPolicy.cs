namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// The 港湾遇袭 run contract: one exact instance per admitted party, a
/// thirty-minute window, and a return to the faction capital when it closes.
/// </summary>
/// <remarks>
/// These are authored Reborn values taken from the requested instance rules, not
/// recovered original-server numbers: the client's own page text states the
/// duration and the return, and the dungeon's monsters and drops are published
/// into the map by the content tooling rather than owned by this run.
/// </remarks>
internal static class HarborAttackPolicy
{
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The largest remaining-time value the client's window may be told, so a
    /// clock correction can never publish more than the run's own limit.
    /// </summary>
    public static readonly int TimeLimitSeconds =
        checked((int)TimeLimit.TotalSeconds);

    /// <summary>
    /// How long members may stay after the run was ended by its leader or ran out
    /// of time. Matches the completed-run leave countdown the other instances
    /// publish, and the members still inside are carried home when it expires.
    /// </summary>
    public static readonly TimeSpan EndExitDelay = TimeSpan.FromSeconds(30);
}
