using Godswar.Server.Application.Rates;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan BattlefieldRoundTickInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The Pindus / Ni Mini settlement clock: every finished round is settled (winner, ranks, what each participant
    /// is owed) and battlefield titles past their days are removed. Like the farm clock it is deliberately not a
    /// critical task: a failure is logged and the next tick tries again.
    /// </summary>
    public async Task RunBattlefieldRoundsAsync(IBattlefieldRoundStore store, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(BattlefieldRoundTickInterval);
        try
        {
            do
            {
                try
                {
                    var days = (int)Math.Clamp(
                        ServerRates.Current.Absolute(ServerRateKeys.BattlefieldTitleDays), 0, 3650);
                    var tick = await store.AdvanceBattlefieldRoundsAsync(DateTimeOffset.UtcNow, days, cancellationToken);
                    if (tick.Settled + tick.TitlesExpired > 0)
                    {
                        Console.WriteLine(
                            $"[battlefield] round clock settled={tick.Settled} rewarded={tick.Rewarded} " +
                            $"titles-expired={tick.TitlesExpired}");
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"[battlefield] round clock tick failed: {error.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
