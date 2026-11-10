using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.FactionCrier;

internal interface IFactionCrierCommandExecutor
{
    Task<FactionCrierExecutionResult> ExecuteAsync(
        CommandEnvelope<FactionCrierCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<FactionCrierExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        FactionCrierReplayIntent replayIntent,
        FactionCrierOperationIdentity identity,
        CancellationToken cancellationToken = default);
}
