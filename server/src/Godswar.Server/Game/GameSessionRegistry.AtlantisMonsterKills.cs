using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal Action? CaptureAtlantisMonsterKill(
        ClientSession session,
        MonsterDamageResult damage)
    {
        if (!_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != damage.Monster.Definition.MapId ||
            !TryGetWorldInstance(context, out var runtime) ||
            !AtlantisEncounterPolicy.IsAtlantisInstance(runtime.Map.Descriptor))
        {
            return null;
        }

        var captured = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map => AtlantisMonsterKillScoring.Capture(
                map, _gameplayCatalogs.Content, context.WorldInstanceId,
                damage));
        if (captured is null)
        {
            return null;
        }

        // Preserve the validated death and original owner across the durable
        // await. Corpse respawn or claimant travel cannot change its identity.
        return () => InvokeWorldOwnerAuthoritativeMutation(
            runtime, map => AtlantisMonsterKillScoring.RecordCommitted(
                map, captured, DateTimeOffset.UtcNow));
    }
}
