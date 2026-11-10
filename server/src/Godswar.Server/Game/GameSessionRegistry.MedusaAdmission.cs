using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private bool MayEnterWorldInstance(
        WorldInstanceRuntime runtime,
        int characterId)
    {
        var admission = InvokeWorldOwner(
            runtime,
            map => (!map.TryGetAtlantisRunSnapshot(out var atlantis) ||
                    atlantis.State == AtlantisRunState.Active) &&
                map.CheckMedusaCharacterAdmission(characterId).MayEnter &&
                // Wonderland is admission-only: its instance is created for one
                // reserved party, so a transfer that is not on that party's
                // roster must never enter the run. Every other map returns true.
                MayEnterWonderlandMap(map, characterId));
        return admission;
    }

    private void RequireWorldInstanceAdmission(
        GameSessionContext context)
    {
        var runtime = GetRequiredWorldInstance(context);
        if (!MayEnterWorldInstance(runtime, context.CharacterId))
        {
            throw new InvalidOperationException(
                $"Character {context.CharacterId} is not admitted to " +
                $"world instance {context.WorldInstanceId}.");
        }
    }
}
