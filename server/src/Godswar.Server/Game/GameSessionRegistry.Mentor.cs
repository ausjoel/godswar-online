using System.Collections.Concurrent;
using Godswar.Server.Application.Mentor;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // ECONOMY mentor: the store, open invitations (apprentice id -> master id + expiry) and characters whose handler
    // should look for a graduation or an owed reward at its next packet. Nothing here runs another session's code:
    // each character's own handler does its own work, under its own character-state gate.
    private IMentorStore? _mentor;
    private readonly ConcurrentDictionary<int, (int MasterId, DateTimeOffset Expires)> _mentorInvitations = [];
    private readonly ConcurrentDictionary<int, byte> _mentorDue = [];

    internal IMentorStore? Mentor => _mentor;

    internal void ConfigureMentor(IMentorStore? store)
    {
        if (store is null) return;
        var previous = Interlocked.CompareExchange(ref _mentor, store, null);
        if (previous is not null && !ReferenceEquals(previous, store))
            throw new InvalidOperationException("Mentor persistence is already configured.");
    }

    internal void OpenMentorInvitation(int apprenticeId, int masterId, DateTimeOffset now) =>
        _mentorInvitations[apprenticeId] = (masterId, now + MentorPolicy.InvitationLifetime);

    internal bool TakeMentorInvitation(int apprenticeId, int masterId, DateTimeOffset now) =>
        _mentorInvitations.TryGetValue(apprenticeId, out var invitation) && invitation.MasterId == masterId &&
        _mentorInvitations.TryRemove(KeyValuePair.Create(apprenticeId, invitation)) && invitation.Expires >= now;

    internal void MarkMentorDue(int characterId) => _mentorDue[characterId] = 0;

    internal bool TakeMentorDue(int characterId) => _mentorDue.TryRemove(characterId, out _);
}
