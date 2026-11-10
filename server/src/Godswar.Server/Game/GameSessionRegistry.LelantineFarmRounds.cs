using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan FarmRoundTickInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The Lelantine Farm round clock. Deliberately not a critical task: a fault here must never take the game
    /// server down, so every failure is logged and the next tick tries again.
    /// </summary>
    public async Task RunLelantineFarmRoundsAsync(ILelantineFarmRoundStore store, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FarmRoundTickInterval);
        try
        {
            do
            {
                try
                {
                    var tick = await store.AdvanceFarmRoundsAsync(DateTimeOffset.UtcNow, cancellationToken);
                    if (tick.Started + tick.Settled > 0)
                    {
                        Console.WriteLine(
                            $"[farm] round clock started={tick.Started} settled={tick.Settled} rewarded={tick.Rewarded}");
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"[farm] round clock tick failed: {error.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
