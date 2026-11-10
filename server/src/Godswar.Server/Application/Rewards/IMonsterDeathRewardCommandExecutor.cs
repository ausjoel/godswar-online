using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Rewards;

internal interface IMonsterDeathRewardCommandExecutor
{
    Task<MonsterDeathRewardExecutionResult> ExecuteAsync(
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        CancellationToken cancellationToken = default);

    // Compatibility adapters can acknowledge the receipt they return. Durable
    // providers override this to observe commit before claimant-only guards.
    async Task<MonsterDeathRewardExecutionResult> ExecuteWithCommitObserverAsync(
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        Action<MonsterDeathRewardExecutionReceipt> onCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onCommitted);
        var result = await ExecuteAsync(envelope, cancellationToken);
        if (result.Receipt is { } receipt)
        {
            onCommitted(receipt);
        }
        return result;
    }
}
