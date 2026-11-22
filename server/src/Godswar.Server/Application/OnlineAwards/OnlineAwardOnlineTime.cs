using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.OnlineAwards;

// B21 item 29 (the admin 2026-11-21): "set the required online time" for the daily Online Award (Athens_132 / Sparta_132).
// The stock award has none (once per realm day). This hot setting asks for N minutes in the world in the current
// session before the claim is accepted; 0 (the default) keeps the stock behaviour. Set it on the editor's Online award
// page or the Rates page; it applies to the next claim, no restart.
internal static class OnlineAwardOnlineTime
{
    public const string Key = "prize.online_award_minutes"; // B21

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        new(Key, Godswar.Server.Application.Finalise2.Finalise2Rates.Category,
            "Online Award: minutes online first", "min", ServerRateKind.Absolute, 0m, 0m, 1440m, 1m, 74,
            "Game/GameClientHandler.OnlineAwardOnlineTime.cs",
            "How long a character must have been in the world (this session) before the daily Online Award can be " +
            "collected. 0 = at once, as in the original game. Logging out starts the count again."),
    ];

    public static int RequiredMinutes() => (int)Math.Clamp(ServerRates.Current.Get(Key), 0m, 1440m);

    /// <summary>Whole minutes still to wait (0 = may claim now). Pure, for the checks.</summary>
    public static int MinutesLeft(DateTimeOffset enteredAt, DateTimeOffset now, int requiredMinutes)
    {
        if (requiredMinutes <= 0) return 0;
        var left = enteredAt.AddMinutes(requiredMinutes) - now;
        return left <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(left.TotalMinutes);
    }
}
