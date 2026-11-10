// ROUND3: rewards read the same runtime EXP projection used by the status composer.
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task<ExperienceBoostState> GetRewardExperienceBoostStateAsync(
        ClientSession session, int accountId, int characterId, byte camp, byte mapId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var durable = await GetExperienceBoostStateAsync(session, accountId, characterId, camp, mapId, now, cancellationToken);
        if (!_playerStatusStates.TryGetValue(session, out var state)) return durable;
        await state.Gate.WaitAsync(cancellationToken);
        try { return RuntimeExperienceBoostPolicy.Merge(durable, state.RuntimeStatuses.Values, now); }
        finally { state.Gate.Release(); }
    }
}
