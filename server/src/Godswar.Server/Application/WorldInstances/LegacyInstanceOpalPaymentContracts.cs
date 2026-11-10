using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal static class LegacyInstanceOpalPaymentPolicy
{
    public const int OpalItemTemplateId = 3932;
    public const short OpalsPerRetryingCharacter = 1;
}

internal sealed record LegacyInstanceOpalPayer(
    int AccountId,
    int CharacterId,
    PlayerOwnershipFence Ownership);

internal sealed record LegacyInstanceOpalChargeRequest(
    Guid ReservationId,
    RealmId RealmId,
    IReadOnlyCollection<LegacyInstanceOpalPayer> Payers,
    DateTimeOffset ChargedAtUtc)
{
    public void Validate()
    {
        if (ReservationId == Guid.Empty ||
            !RealmId.IsValid ||
            ChargedAtUtc == default ||
            ChargedAtUtc.Offset != TimeSpan.Zero ||
            Payers.Count is < 1 or > 5 ||
            Payers.Any(static payer =>
                payer.AccountId <= 0 ||
                payer.CharacterId <= 0 ||
                !payer.Ownership.IsValid) ||
            Payers.Select(static payer => payer.CharacterId)
                .Distinct()
                .Count() != Payers.Count)
        {
            throw new ArgumentException(
                "Invalid Atlantis Opal payment participants.");
        }
    }
}

internal enum LegacyInstanceOpalChargeStatus : byte
{
    Charged = 1,
    InsufficientOpal = 2,
    OwnershipLost = 3
}

internal sealed record LegacyInstanceOpalInventoryMutation(
    int AccountId,
    int CharacterId,
    int KitBagSlot,
    string BeforeCompactItemState,
    string AfterCompactItemState);

internal sealed record LegacyInstanceOpalChargeResult(
    LegacyInstanceOpalChargeStatus Status,
    IReadOnlySet<int> FailedCharacterIds,
    IReadOnlyList<LegacyInstanceOpalInventoryMutation> Mutations);

internal sealed record LegacyInstanceOpalSettlementResult(
    IReadOnlyList<LegacyInstanceOpalInventoryMutation> RefundMutations);

internal interface ILegacyInstanceOpalPaymentStore
{
    Task<LegacyInstanceOpalChargeResult> ChargeAsync(
        LegacyInstanceOpalChargeRequest request,
        CancellationToken cancellationToken = default);

    Task<LegacyInstanceOpalSettlementResult> SettleAsync(
        Guid reservationId,
        IReadOnlyCollection<int> admittedCharacterIds,
        CancellationToken cancellationToken = default);

    Task RecordAdmissionsAsync(
        Guid reservationId,
        IReadOnlyCollection<int> admittedCharacterIds,
        CancellationToken cancellationToken = default);

    Task<int> RecoverPendingAsync(
        RealmId realmId,
        DateTimeOffset staleBeforeUtc,
        CancellationToken cancellationToken = default);

    Task<int> RecoverCharacterAsync(
        RealmId realmId,
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default);
}
