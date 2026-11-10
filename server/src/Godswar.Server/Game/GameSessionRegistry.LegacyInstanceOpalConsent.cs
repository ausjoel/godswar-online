using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan LegacyInstanceOpalConsentLifetime =
        TimeSpan.FromMinutes(2);

    private readonly Dictionary<int, LegacyInstanceOpalRetryConsent>
        _legacyInstanceOpalRetryConsents = [];

    internal LegacyInstanceEntryStatus
        TryRecordLegacyInstanceOpalRetryConsent(
        ClientSession requestingSession,
        InstanceCallerEntryDestination destination,
        float npcX,
        float npcZ,
        float maximumDistance,
        DateTimeOffset now,
        out LegacyInstancePartySnapshot snapshot,
        out bool requesterIsLeader)
    {
        ArgumentNullException.ThrowIfNull(requestingSession);
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Kind != InstanceCallerEntryKind.Atlantis ||
            destination.PaymentMode !=
                InstanceCallerEntryPaymentMode.OpalRetry)
        {
            throw new ArgumentException(
                "Opal consent is only valid for the paid Atlantis action.",
                nameof(destination));
        }

        lock (_gate)
        {
            RemoveExpiredLegacyInstanceOpalConsentsLocked(now);
            var status = TryCaptureLegacyInstancePartyLocked(
                requestingSession,
                destination,
                npcX,
                npcZ,
                maximumDistance,
                requireLeader: false,
                out snapshot,
                out requesterIsLeader);
            if (status != LegacyInstanceEntryStatus.Ready)
            {
                return status;
            }

            var requester = snapshot.Members.Single(member =>
                ReferenceEquals(member.Session, requestingSession));
            _legacyInstanceOpalRetryConsents[requester.CharacterId] = new(
                requester.Session,
                snapshot,
                now + LegacyInstanceOpalConsentLifetime);
            return LegacyInstanceEntryStatus.Ready;
        }
    }

    internal LegacyInstanceOpalConsentValidationStatus
        TryConsumeLegacyInstanceOpalRetryConsents(
        LegacyInstancePartySnapshot snapshot,
        InstanceCallerEntryDestination destination,
        float npcX,
        float npcZ,
        float maximumDistance,
        IReadOnlySet<int> paymentRequiredCharacterIds,
        DateTimeOffset now,
        out int[] missingCharacterIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(paymentRequiredCharacterIds);

        lock (_gate)
        {
            RemoveExpiredLegacyInstanceOpalConsentsLocked(now);
            if (!IsLegacyInstancePartySnapshotCurrentLocked(
                    snapshot,
                    destination,
                    npcX,
                    npcZ,
                    maximumDistance))
            {
                missingCharacterIds = [];
                return LegacyInstanceOpalConsentValidationStatus
                    .PartyChanged;
            }

            var partyCharacterIds = snapshot.Members
                .Select(static member => member.CharacterId)
                .ToHashSet();
            if (!paymentRequiredCharacterIds.IsSubsetOf(
                    partyCharacterIds))
            {
                missingCharacterIds = [];
                return LegacyInstanceOpalConsentValidationStatus
                    .PartyChanged;
            }

            missingCharacterIds = paymentRequiredCharacterIds
                .Where(characterId =>
                    !_legacyInstanceOpalRetryConsents.TryGetValue(
                        characterId,
                        out var consent) ||
                    consent.ExpiresAt <= now ||
                    !IsSameLegacyInstancePartySnapshot(
                        consent.Party,
                        snapshot))
                .Order()
                .ToArray();
            if (missingCharacterIds.Length != 0)
            {
                return LegacyInstanceOpalConsentValidationStatus
                    .MissingConsent;
            }

            RemoveLegacyInstanceOpalConsentsForPartyLocked(snapshot);
            return LegacyInstanceOpalConsentValidationStatus.Ready;
        }
    }

    internal void ClearLegacyInstanceOpalRetryConsents(
        LegacyInstancePartySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            RemoveLegacyInstanceOpalConsentsForPartyLocked(snapshot);
        }
    }

    internal bool IsLegacyInstancePartySnapshotCurrent(
        LegacyInstancePartySnapshot snapshot,
        InstanceCallerEntryDestination destination,
        float npcX,
        float npcZ,
        float maximumDistance)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            return IsLegacyInstancePartySnapshotCurrentLocked(
                snapshot,
                destination,
                npcX,
                npcZ,
                maximumDistance);
        }
    }

    internal bool IsLegacyInstancePartyMemberSnapshotCurrent(
        LegacyInstancePartySnapshot party,
        LegacyInstancePartyMember member,
        float npcX,
        float npcZ,
        float maximumDistance)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(member);
        lock (_gate)
        {
            if (!TryFindCurrentContextLocked(
                    member.CharacterId,
                    out var current) ||
                !ReferenceEquals(current.Session, member.Session) ||
                current.AccountId != member.AccountId ||
                current.RealmId != member.RealmId ||
                current.Ownership != member.Ownership ||
                !IsLegacyInstanceEntryReady(
                    current,
                    member.SourceWorldInstanceId,
                    member.SourceMapId,
                    npcX,
                    npcZ,
                    maximumDistance) ||
                !_partiesByCharacter.TryGetValue(
                    member.CharacterId,
                    out var currentParty))
            {
                return false;
            }

            NormalizePartyLocked(currentParty);
            return _partiesByCharacter.TryGetValue(
                    member.CharacterId,
                    out currentParty) &&
                currentParty.Id == party.PartyId &&
                currentParty.MemberCharacterIds.SequenceEqual(
                    party.Members.Select(static item => item.CharacterId));
        }
    }

    private bool IsLegacyInstancePartySnapshotCurrentLocked(
        LegacyInstancePartySnapshot snapshot,
        InstanceCallerEntryDestination destination,
        float npcX,
        float npcZ,
        float maximumDistance)
    {
        var leader = snapshot.Members.FirstOrDefault(member =>
            member.CharacterId == snapshot.LeaderCharacterId);
        if (leader is null)
        {
            return false;
        }

        var status = TryCaptureLegacyInstancePartyLocked(
            leader.Session,
            destination,
            npcX,
            npcZ,
            maximumDistance,
            requireLeader: true,
            out var current,
            out _);
        return status == LegacyInstanceEntryStatus.Ready &&
            IsSameLegacyInstancePartySnapshot(snapshot, current);
    }

    private static bool IsSameLegacyInstancePartySnapshot(
        LegacyInstancePartySnapshot left,
        LegacyInstancePartySnapshot right)
    {
        if (left.PartyId != right.PartyId ||
            left.RealmId != right.RealmId ||
            left.LeaderCharacterId != right.LeaderCharacterId ||
            left.Members.Count != right.Members.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Members.Count; index++)
        {
            var expected = left.Members[index];
            var current = right.Members[index];
            if (!ReferenceEquals(expected.Session, current.Session) ||
                expected.AccountId != current.AccountId ||
                expected.CharacterId != current.CharacterId ||
                expected.RealmId != current.RealmId ||
                expected.SourceWorldInstanceId !=
                    current.SourceWorldInstanceId ||
                expected.SourceMapId != current.SourceMapId ||
                expected.Ownership != current.Ownership)
            {
                return false;
            }
        }
        return true;
    }

    private void RemoveExpiredLegacyInstanceOpalConsentsLocked(
        DateTimeOffset now)
    {
        foreach (var characterId in _legacyInstanceOpalRetryConsents
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _legacyInstanceOpalRetryConsents.Remove(characterId);
        }
    }

    private void RemoveLegacyInstanceOpalConsentsForPartyLocked(
        LegacyInstancePartySnapshot snapshot)
    {
        foreach (var member in snapshot.Members)
        {
            if (_legacyInstanceOpalRetryConsents.TryGetValue(
                    member.CharacterId,
                    out var consent) &&
                IsSameLegacyInstancePartySnapshot(consent.Party, snapshot))
            {
                _legacyInstanceOpalRetryConsents.Remove(
                    member.CharacterId);
            }
        }
    }

    private sealed record LegacyInstanceOpalRetryConsent(
        ClientSession Session,
        LegacyInstancePartySnapshot Party,
        DateTimeOffset ExpiresAt);
}
