using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal Task<WonderlandChestClaimReceipt> ClaimWonderlandBossLootAsync(WonderlandBossLootClaimRequest request,
        CancellationToken token) => _wonderlandChests?.ClaimBossAsync(request, token) ??
        Task.FromResult(new WonderlandChestClaimReceipt(WonderlandChestClaimStatus.Unavailable, 0, []));
}
