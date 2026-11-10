using Godswar.Server.Application.Characters;

namespace Godswar.Server.Game;

internal static class MonsterDeathRewardCommitBoundary
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> commit,
        bool allowImmediateReplay,
        Action<Exception>? onImmediateReplay = null,
        Action<T>? onSettled = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        T settlement;
        try
        {
            settlement = await commit(CancellationToken.None);
        }
        catch (Exception firstFailure)
            when (allowImmediateReplay &&
                  firstFailure is not
                      PlayerOwnershipValidationException)
        {
            onImmediateReplay?.Invoke(firstFailure);
            settlement = await commit(CancellationToken.None);
        }

        // Committed in-memory encounter effects follow a successful receipt.
        // Their failures must never replay the durable reward transaction.
        onSettled?.Invoke(settlement);
        return settlement;
    }
}
