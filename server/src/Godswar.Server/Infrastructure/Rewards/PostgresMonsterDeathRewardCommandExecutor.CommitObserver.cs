using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Rewards;

namespace Godswar.Server.Infrastructure.Rewards;

internal sealed partial class PostgresMonsterDeathRewardCommandExecutor
{
    public Task<MonsterDeathRewardExecutionResult> ExecuteAsync(
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(envelope, onCommitted: null, cancellationToken);

    public Task<MonsterDeathRewardExecutionResult> ExecuteWithCommitObserverAsync(
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        Action<MonsterDeathRewardExecutionReceipt> onCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onCommitted);
        return ExecuteCoreAsync(envelope, onCommitted, cancellationToken);
    }
}
