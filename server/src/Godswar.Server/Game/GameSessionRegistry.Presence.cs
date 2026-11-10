using Godswar.Server.Networking;

namespace Godswar.Server.Game;

/// <summary>
/// Presence questions the guild window needs: the client counts a guild's
/// online members from the per-member flag the server sends, so the flag has to
/// come from the live sessions rather than from persisted state.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    internal bool IsCharacterOnline(int characterId) =>
        TryGetCharacterSession(characterId, out _);

    /// <summary>
    /// Finds the session a character is being played from.
    /// </summary>
    /// <remarks>
    /// A guild message about one member has to reach that member's own client -
    /// a roster the server repaints for everyone still in the guild is only
    /// useful if it can be addressed to them individually - so this is the one
    /// lookup both that and the online flag use. Only a session that has entered
    /// the world is counted: a character still in character select has no window
    /// to repaint and cannot be listed as online.
    /// </remarks>
    internal bool TryGetCharacterSession(int characterId, out ClientSession session)
    {
        session = null!;
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
                    session = candidate.Key;
                    return true;
                }
            }
        }

        return false;
    }
}
