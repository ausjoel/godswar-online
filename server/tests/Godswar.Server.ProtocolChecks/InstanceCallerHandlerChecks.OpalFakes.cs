using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private sealed class ScriptedLegacyInstanceDailyEntryStore :
        ILegacyInstanceDailyEntryClaimStore
    {
        public IReadOnlySet<int> PaymentRequiredIndexes { get; set; } =
            new HashSet<int>();

        public LegacyInstanceDailyEntryClaimStatus ClaimStatus { get; set; } =
            LegacyInstanceDailyEntryClaimStatus.Claimed;

        public ushort DailyEntryLimit { get; set; } = 4;

        public List<LegacyInstanceDailyEntryClaimRequest> Claims { get; } =
            [];

        public List<Guid> FullReleases { get; } = [];

        public List<ReleasedLegacyInstanceMembers> MemberReleases { get; } =
            [];

        public List<RecordedLegacyInstanceAdmissions> Admissions { get; } =
            [];
        public List<RecordedLegacyInstanceAdmissions> AdmissionAttempts { get; } = [];
        public int AdmissionFailuresRemaining { get; set; }

        public Task<LegacyInstanceDailyEntryClaimResult> TryClaimAsync(
            LegacyInstanceDailyEntryClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            Claims.Add(request);
            var characterIds = request.CharacterIds.ToArray();
            var payers = ClaimStatus ==
                    LegacyInstanceDailyEntryClaimStatus.Claimed
                ? PaymentRequiredIndexes
                    .Select(index => characterIds[index])
                    .ToHashSet()
                : [];
            return Task.FromResult(new LegacyInstanceDailyEntryClaimResult(
                ClaimStatus,
                DailyEntryLimit: DailyEntryLimit,
                FreeEntryLimit: 3,                payers,
                PaidRetryLimit: 1));
        }

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FullReleases.Add(reservationId);
            return Task.CompletedTask;
        }

        public Task ReleaseMembersAsync(
            Guid reservationId,
            IReadOnlyCollection<int> characterIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MemberReleases.Add(new(
                reservationId,
                characterIds.ToHashSet()));
            return Task.CompletedTask;
        }

        public Task RecordAdmissionsAsync(
            Guid reservationId,
            IReadOnlyCollection<int> admittedCharacterIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdmissionAttempts.Add(new(reservationId, admittedCharacterIds.ToHashSet()));
            if (AdmissionFailuresRemaining > 0)
            {
                AdmissionFailuresRemaining--;
                return Task.FromException(new IOException("admission marker temporarily unavailable"));
            }
            Admissions.Add(new(
                reservationId,
                admittedCharacterIds.ToHashSet()));
            return Task.CompletedTask;
        }

        public Task<int> RecoverPendingAsync(
            RealmId realmId,
            DateTimeOffset staleBeforeUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        public Task<int> RecoverCharacterAsync(
            RealmId realmId,
            int accountId,
            int characterId,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }
    }

    private sealed class ScriptedLegacyInstanceOpalPaymentStore :
        ILegacyInstanceOpalPaymentStore
    {
        public LegacyInstanceOpalChargeStatus ChargeStatus { get; set; } =
            LegacyInstanceOpalChargeStatus.Charged;

        public Func<LegacyInstanceOpalChargeRequest,
            IReadOnlyList<LegacyInstanceOpalInventoryMutation>>
            MutationFactory { get; set; } = static _ => [];

        public Func<Guid, IReadOnlyCollection<int>,
            IReadOnlyList<LegacyInstanceOpalInventoryMutation>>
            RefundFactory { get; set; } = static (_, _) => [];

        public Func<LegacyInstanceOpalChargeRequest, Task>?
            BeforeChargeCompletesAsync { get; set; }

        public List<LegacyInstanceOpalChargeRequest> Charges { get; } = [];

        public List<SettledLegacyInstanceOpals> Settlements { get; } = [];

        public List<RecordedLegacyInstanceAdmissions> Admissions { get; } =
            [];

        public async Task<LegacyInstanceOpalChargeResult> ChargeAsync(
            LegacyInstanceOpalChargeRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            Charges.Add(request);
            if (BeforeChargeCompletesAsync is not null)
            {
                await BeforeChargeCompletesAsync(request);
                cancellationToken.ThrowIfCancellationRequested();
            }
            var mutations = ChargeStatus ==
                LegacyInstanceOpalChargeStatus.Charged
                    ? MutationFactory(request)
                    : [];
            var failures = ChargeStatus ==
                LegacyInstanceOpalChargeStatus.Charged
                    ? new HashSet<int>()
                    : request.Payers
                        .Select(static payer => payer.CharacterId)
                        .ToHashSet();
            return new LegacyInstanceOpalChargeResult(
                ChargeStatus,
                failures,
                mutations);
        }

        public Task<LegacyInstanceOpalSettlementResult> SettleAsync(
            Guid reservationId,
            IReadOnlyCollection<int> admittedCharacterIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var admitted = admittedCharacterIds.ToHashSet();
            Settlements.Add(new(reservationId, admitted));
            return Task.FromResult(
                new LegacyInstanceOpalSettlementResult(
                    RefundFactory(reservationId, admitted)));
        }

        public Task RecordAdmissionsAsync(
            Guid reservationId,
            IReadOnlyCollection<int> admittedCharacterIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Admissions.Add(new(
                reservationId,
                admittedCharacterIds.ToHashSet()));
            return Task.CompletedTask;
        }

        public Task<int> RecoverPendingAsync(
            RealmId realmId,
            DateTimeOffset staleBeforeUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        public Task<int> RecoverCharacterAsync(
            RealmId realmId,
            int accountId,
            int characterId,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }
    }

    private sealed record ReleasedLegacyInstanceMembers(
        Guid ReservationId,
        IReadOnlySet<int> CharacterIds);

    private sealed record SettledLegacyInstanceOpals(
        Guid ReservationId,
        IReadOnlySet<int> AdmittedCharacterIds);

    private sealed record RecordedLegacyInstanceAdmissions(
        Guid ReservationId,
        IReadOnlySet<int> CharacterIds);
}
