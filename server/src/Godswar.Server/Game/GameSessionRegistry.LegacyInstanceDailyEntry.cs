using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly Dictionary<(
        int RealmId,
        DateOnly Day,
        InstanceCallerEntryKind InstanceKind,
        int CharacterId), HashSet<Guid>>
        _localLegacyInstanceDailyEntries = [];

    internal LegacyInstanceDailyEntryClaimResult
        TryReserveLocalLegacyInstanceDailyEntry(
        Guid reservationId,
        RealmId realmId,
        DateOnly day,
        InstanceCallerEntryKind instanceKind,
        IReadOnlyCollection<int> characterIds)
    {
        if (reservationId == Guid.Empty ||
            !realmId.IsValid ||
            !Enum.IsDefined(instanceKind))
        {
            throw new ArgumentException(
                "A local legacy-instance reservation requires valid " +
                "identities.");
        }
        ArgumentNullException.ThrowIfNull(characterIds);

        if (characterIds.Count is < 1 or > 5 ||
            characterIds.Any(static id => id <= 0))
        {
            throw new ArgumentException(
                "A local legacy-instance reservation requires a valid " +
                "party.",
                nameof(characterIds));
        }

        lock (_gate)
        {
            var freeEntryLimit = LegacyInstanceDailyEntryPolicy
                .GetDefaultFreeEntryLimit(instanceKind);
            var paidRetryLimit = LegacyInstanceDailyEntryPolicy
                .GetDefaultPaidRetryLimit(instanceKind);
            var dailyEntryLimit = LegacyInstanceDailyEntryPolicy
                .GetDefaultDailyEntryLimit(instanceKind);
            foreach (var stale in _localLegacyInstanceDailyEntries.Keys
                         .Where(key =>
                             key.RealmId == realmId.Value &&
                             key.Day != day)
                         .ToArray())
            {
                _localLegacyInstanceDailyEntries.Remove(stale);
            }

            var keys = characterIds.Distinct()
                .Select(characterId => (
                    realmId.Value,
                    day,
                    instanceKind,
                    characterId))
                .ToArray();
            if (keys.Length != characterIds.Count ||
                _localLegacyInstanceDailyEntries.Values.Any(
                    reservations => reservations.Contains(reservationId)) ||
                keys.Any(key =>
                    _localLegacyInstanceDailyEntries.TryGetValue(
                        key,
                        out var reservations) &&
                    dailyEntryLimit is ushort limit &&
                    reservations.Count >= limit))
            {
                return new(
                    LegacyInstanceDailyEntryClaimStatus.DailyLimitReached,
                    dailyEntryLimit,
                    freeEntryLimit,
                    new HashSet<int>(),
                    paidRetryLimit);
            }

            IReadOnlySet<int> paymentRequiredCharacterIds =
                paidRetryLimit is null
                    ? new HashSet<int>()
                    : keys
                        .Where(key =>
                            _localLegacyInstanceDailyEntries.TryGetValue(
                                key,
                                out var reservations) &&
                            reservations.Count >= freeEntryLimit)
                        .Select(static key => key.characterId)
                        .ToHashSet();

            foreach (var key in keys)
            {
                if (!_localLegacyInstanceDailyEntries.TryGetValue(
                        key,
                        out var reservations))
                {
                    reservations = [];
                    _localLegacyInstanceDailyEntries.Add(key, reservations);
                }
                reservations.Add(reservationId);
            }
            return new(
                LegacyInstanceDailyEntryClaimStatus.Claimed,
                dailyEntryLimit,
                freeEntryLimit,
                paymentRequiredCharacterIds,
                paidRetryLimit);
        }
    }

    internal void ReleaseLocalLegacyInstanceDailyEntry(Guid reservationId)
    {
        if (reservationId == Guid.Empty)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var key in _localLegacyInstanceDailyEntries.Keys
                         .ToArray())
            {
                var reservations = _localLegacyInstanceDailyEntries[key];
                reservations.Remove(reservationId);
                if (reservations.Count == 0)
                {
                    _localLegacyInstanceDailyEntries.Remove(key);
                }
            }
        }
    }

    internal void ReleaseLocalLegacyInstanceDailyEntryMembers(
        Guid reservationId,
        IReadOnlyCollection<int> characterIds)
    {
        if (reservationId == Guid.Empty)
        {
            return;
        }
        ArgumentNullException.ThrowIfNull(characterIds);
        var distinctCharacterIds = characterIds.Distinct().ToHashSet();
        if (distinctCharacterIds.Count == 0)
        {
            return;
        }
        if (distinctCharacterIds.Count != characterIds.Count ||
            distinctCharacterIds.Any(static characterId => characterId <= 0))
        {
            throw new ArgumentException(
                "A partial local legacy-instance release requires " +
                "unique, positive character IDs.",
                nameof(characterIds));
        }

        lock (_gate)
        {
            foreach (var pair in _localLegacyInstanceDailyEntries
                         .Where(pair =>
                             distinctCharacterIds.Contains(
                                 pair.Key.CharacterId))
                         .ToArray())
            {
                pair.Value.Remove(reservationId);
                if (pair.Value.Count == 0)
                {
                    _localLegacyInstanceDailyEntries.Remove(pair.Key);
                }
            }
        }
    }
}
