namespace Godswar.Server.Game;

/// <summary>
/// A guild action that changes one character's guild has to reach players other
/// than the one whose handler is running: the guild name and duty live on the
/// player's own object, which the other clients were told about when the player
/// became visible (<c>10199</c>). Joining a guild therefore has to repeat that
/// frame for their map.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// The live context of a character by id, with the map and object id its
    /// updates have to be addressed to.
    /// </summary>
    internal bool TryGetCharacterContext(
        int characterId,
        out GameSessionContext context)
    {
        context = null!;
        if (characterId <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            foreach (var candidate in _sessions)
            {
                if (candidate.Value.CharacterId == characterId &&
                    candidate.Value.WorldReady)
                {
                    context = candidate.Value;
                    return true;
                }
            }
        }

        return false;
    }
}
