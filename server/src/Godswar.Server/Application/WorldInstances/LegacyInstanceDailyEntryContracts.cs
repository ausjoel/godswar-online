using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Application.Characters;

namespace Godswar.Server.Application.WorldInstances;

internal enum LegacyInstanceDailyEntryClaimStatus : byte
{
    Claimed = 1,
    DailyLimitReached = 2,
    AlreadyUsed = DailyLimitReached
}

internal static class LegacyInstanceDailyEntryPolicy
{
    public const ushort DefaultFreeEntryLimit = 3;

    public static ushort? GetDefaultPaidRetryLimit(
        InstanceCallerEntryKind instanceKind) => instanceKind switch
    {
        InstanceCallerEntryKind.Atlantis => 1,
        // Unlimited 飘渺幻境 entries by the operator's decision (2026-09-28). The
        // reference allowed three free entries a day and no paid retry, which
        // blocked a fourth attempt; a null retry limit is what both claim paths
        // read as "no daily entry limit", so they stop counting and stop marking
        // characters as owing an opal.
        // FINALISE2 (the admin F-22): back to one entry a day and no paid retry. The live row in
        // legacy_instance_settings decides on Postgres (data step f2-p1-wonderland-one-a-day.sql); this default is
        // the local/JSON stores' copy.
        InstanceCallerEntryKind.Wonderland => 0,
        // 港湾遇袭 has no paid retry at all.
        InstanceCallerEntryKind.HarborAttack => 0,
        InstanceCallerEntryKind.TrainingGround => 0, // TRAINING: IGG acid=166, no paid entry
        _ when WarriorTrainingGroundPolicy.IsWarriorKind(instanceKind) => 0, // WARRIOR (F-51): no paid entry
        _ => throw new ArgumentOutOfRangeException(nameof(instanceKind))
    };

    /// <summary>
    /// Free entries a day for a kind whose policy has not been published to the
    /// database yet. 港湾遇袭 grants the single daily opportunity its own client
    /// text states; the reviewed instances keep the shared default.
    /// </summary>
    public static ushort GetDefaultFreeEntryLimit(
        InstanceCallerEntryKind instanceKind) => instanceKind switch
    {
        InstanceCallerEntryKind.HarborAttack => 1,
        InstanceCallerEntryKind.TrainingGround => 1, // TRAINING: IGG acid=166, one a day
        _ when WarriorTrainingGroundPolicy.IsWarriorKind(instanceKind) => 1, // WARRIOR (F-51): wiki, each ground once a day
        InstanceCallerEntryKind.Wonderland => 1, // FINALISE2 (the admin F-22): NF_EM_T211, once a day before 23:00
        _ => DefaultFreeEntryLimit
    };

    public static ushort? GetDefaultDailyEntryLimit(
        InstanceCallerEntryKind instanceKind)
    {
        var paidRetryLimit = GetDefaultPaidRetryLimit(instanceKind);
        return paidRetryLimit is ushort paid
            ? checked((ushort)(GetDefaultFreeEntryLimit(instanceKind) + paid))
            : null;
    }
}

internal sealed record LegacyInstanceDailyEntryClaimResult(
    LegacyInstanceDailyEntryClaimStatus Status,
    ushort? DailyEntryLimit,
    ushort FreeEntryLimit,
    IReadOnlySet<int> PaymentRequiredCharacterIds,
    ushort? PaidRetryLimit = 0);

internal sealed record LegacyInstanceDailyEntryClaimRequest(
    Guid ReservationId,
    RealmId RealmId,
    DateOnly RealmDay,
    InstanceCallerEntryKind InstanceKind,
    IReadOnlyCollection<int> CharacterIds,
    DateTimeOffset ClaimedAtUtc)
{
    public void Validate()
    {
        if (ReservationId == Guid.Empty ||
            !RealmId.IsValid ||
            ClaimedAtUtc == default ||
            ClaimedAtUtc.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(InstanceKind))
        {
            throw new ArgumentException(
                "Invalid legacy-instance daily-entry claim identity.");
        }

        if (CharacterIds.Count is < 1 or > 5 ||
            CharacterIds.Any(static id => id <= 0) ||
            CharacterIds.Distinct().Count() != CharacterIds.Count)
        {
            throw new ArgumentException(
                "The legacy-instance claim requires a valid, unique party.");
        }
    }
}

internal interface ILegacyInstanceDailyEntryClaimStore
{
    Task<LegacyInstanceDailyEntryClaimResult> TryClaimAsync(
        LegacyInstanceDailyEntryClaimRequest request,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default);

    Task ReleaseMembersAsync(
        Guid reservationId,
        IReadOnlyCollection<int> characterIds,
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
