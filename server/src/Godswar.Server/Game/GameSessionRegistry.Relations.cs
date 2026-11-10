using System.Collections.Concurrent;

namespace Godswar.Server.Game;

/// <summary>
/// Who is online for the friend windows, and the friend requests waiting for an
/// answer. A request is only held in memory: it does not outlive the process and it
/// expires, so an answer can never be accepted for a request that was not made.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan RelationInvitationLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(int Target, int Requester), DateTime>
        _relationInvitations = new();

    internal bool TryFindOnlineRelationTarget(
        Domain.World.Instances.RealmId realmId,
        string name,
        out GameSessionContext target)
    {
        lock (_gate)
        {
            return TryFindOnlinePartyMemberLocked(name, realmId, out target);
        }
    }

    internal bool TryFindOnlineByCharacterId(
        int characterId,
        out GameSessionContext context)
    {
        lock (_gate)
        {
            return TryFindOnlinePartyMemberByIdLocked(characterId, out context);
        }
    }

    internal void RecordRelationInvitation(int targetCharacterId, int requesterCharacterId)
    {
        var now = DateTime.UtcNow;
        foreach (var invitation in _relationInvitations)
        {
            if (invitation.Value <= now)
            {
                _relationInvitations.TryRemove(invitation);
            }
        }

        _relationInvitations[(targetCharacterId, requesterCharacterId)] =
            now + RelationInvitationLifetime;
    }

    internal bool TryTakeRelationInvitation(int targetCharacterId, int requesterCharacterId) =>
        _relationInvitations.TryRemove(
            (targetCharacterId, requesterCharacterId),
            out var expires) &&
        expires > DateTime.UtcNow;
}
