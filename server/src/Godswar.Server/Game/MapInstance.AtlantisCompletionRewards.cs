using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private Guid _atlantisRewardReservation;
    private readonly HashSet<Guid> _atlantisRewardReservations = [];
    private IReadOnlyDictionary<int, AtlantisCompletionMember> _atlantisRewardCandidates =
        new Dictionary<int, AtlantisCompletionMember>();
    private readonly HashSet<int> _atlantisRewardAdmissions = [];
    private AtlantisCompletionEvidence? _atlantisCompletionEvidence;

    internal void ConfigureAtlantisRewardAdmission(Guid reservationId,
        IReadOnlyList<LegacyInstancePartyMember> members)
    {
        lock (_atlantisEncounterGate)
        {
            if (reservationId == Guid.Empty || members.Count is < 1 or > 5 ||
                _atlantisRewardReservation != Guid.Empty && _atlantisRewardReservation != reservationId)
            {
                throw new InvalidOperationException("Invalid Atlantis admission reward identity.");
            }
            if (_atlantisRewardReservation != Guid.Empty) return;
            _atlantisRewardReservation = reservationId;
            _atlantisRewardReservations.Add(reservationId);
            _atlantisRewardCandidates = members.ToDictionary(member => member.CharacterId,
                member => new AtlantisCompletionMember(member.AccountId, member.CharacterId));
        }
    }

    internal void RecordAtlantisRewardAdmissions(Guid reservationId, IReadOnlyCollection<int> characterIds)
    {
        lock (_atlantisEncounterGate)
        {
            if (_atlantisCompletionEvidence is not null || reservationId == Guid.Empty) return;
            // A member who confirmed the party window after the leader committed
            // claimed their own daily attempt, so this run owns more than one
            // admission reservation. The settlement validates the ledger against
            // all of them.
            _atlantisRewardReservations.Add(reservationId);
            if (_atlantisRewardReservation != reservationId) return;
            foreach (var characterId in characterIds)
            {
                if (_atlantisRewardCandidates.ContainsKey(characterId)) _atlantisRewardAdmissions.Add(characterId);
            }
        }
    }

    // Called by the world owner the first time the run reaches any terminal
    // state - completed at point850, cancelled by its leader, or timed out.
    // The award itself is the instance's result at that moment and keeps the
    // registered party's classification; the recipients are every character
    // still inside the instance at that moment. A member who confirmed the party
    // window after the leader is therefore entitled exactly like the leader,
    // while a character who left or dropped before this moment is not
    // ("offline is not present").
    private void FreezeAtlantisRewardMembers(AtlantisRunSnapshot run)
    {
        if (_atlantisCompletionEvidence is not null || _atlantisRewardReservation == Guid.Empty) return;
        var admitted = _atlantisRewardAdmissions.Order().Select(id => _atlantisRewardCandidates[id]).ToArray();
        var eligible = Snapshot().Where(context => context.WorldReady &&
                context.WorldInstanceId == run.WorldInstanceId && context.Ownership.IsValid)
            .OrderBy(context => context.CharacterId)
            .Select(context => new AtlantisCompletionIdentity(context.AccountId, context.CharacterId,
                context.CharacterName, context.Character.Camp)).ToArray();
        if (eligible.Length is < 1 or > 5)
        {
            _atlantisCompletionEvidence = new(null, []);
            return;
        }
        _atlantisCompletionEvidence = new(new AtlantisCompletionRewardRequest(run.WorldInstanceId,
            Descriptor.RealmId, _atlantisRewardReservation, run.StartedAt.ToUniversalTime(),
            run.TerminalAt!.Value.ToUniversalTime(), run.TeamPoints, admitted,
            eligible.Select(member => new AtlantisCompletionMember(member.AccountId, member.CharacterId)).ToArray(),
            [.. _atlantisRewardReservations]),
            Array.AsReadOnly(eligible));
    }

    internal AtlantisCompletionEvidence? GetAtlantisCompletionEvidence()
    {
        lock (_atlantisEncounterGate) return _atlantisCompletionEvidence;
    }

    /// <summary>
    /// Whether this account and character are on the run's own admitted roster:
    /// the registered party member who claimed and confirmed their entry.
    /// </summary>
    /// <remarks>
    /// The same recorded admission the reward settlement and the pet capture
    /// read, exposed for the login reconnect: only a member who actually came in
    /// is put back inside a running Atlantis.
    /// </remarks>
    internal bool IsAdmittedAtlantisMember(int accountId, int characterId)
    {
        lock (_atlantisEncounterGate)
        {
            return _atlantisRewardReservation != Guid.Empty &&
                _atlantisRewardAdmissions.Contains(characterId) &&
                _atlantisRewardCandidates.TryGetValue(characterId,
                    out var member) &&
                member.AccountId == accountId;
        }
    }

    internal bool HasAdmittedAtlantisPetOwner(int accountId, int characterId)
    {
        lock (_atlantisEncounterGate)
        {
            return _atlantisRewardReservation != Guid.Empty &&
                _atlantisRewardAdmissions.Contains(characterId) &&
                _atlantisRewardCandidates.TryGetValue(characterId, out var member) &&
                member.AccountId == accountId &&
                TryGetAtlantisRunSnapshot(out _);
        }
    }
}

internal sealed record AtlantisCompletionIdentity(int AccountId, int CharacterId, string Name, byte Camp);
internal sealed record AtlantisCompletionEvidence(AtlantisCompletionRewardRequest? Request,
    IReadOnlyList<AtlantisCompletionIdentity> Members);
