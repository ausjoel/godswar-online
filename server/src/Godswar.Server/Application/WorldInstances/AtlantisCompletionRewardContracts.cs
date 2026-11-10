using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct AtlantisCompletionMember(int AccountId, int CharacterId);

internal readonly record struct AtlantisCompletionRewardAward(int HardPoints, uint TitleId, string TitleName);

internal static class AtlantisCompletionRewardPolicy
{
    public const string Revision = "atlantis-completion-v2";
    public const int CompletedHardPoints = 2800;

    /// <summary>Team points that complete the run.</summary>
    public const int CompletionTeamPoints = 850;

    public const uint SeabedExplorerTitleId = 5013;
    public const uint DeepSeaHunterTitleId = 5014;

    /// <summary>
    /// Team points to HardPoints for a run that ends before completion.
    /// </summary>
    /// <remarks>
    /// Published operator table. A tier is earned by reaching its score, so a
    /// score between tiers takes the highest tier at or below it ("floor"): 63
    /// points pay the 50 tier, 720 pay the 700 tier.
    /// </remarks>
    private static readonly (int Score, int HardPoints)[] IncompleteTiers =
    [
        (0, 200),
        (50, 600),
        (80, 900),
        (100, 1_000),
        (150, 1_200),
        (180, 1_300),
        (220, 1_420),
        (250, 1_540),
        (350, 1_660),
        (400, 1_820),
        (500, 2_000),
        (600, 2_200),
        (700, 2_400)
    ];

    /// <summary>
    /// The award an ending run earned: the completed party award at the
    /// completion threshold, otherwise the highest incomplete tier at or below
    /// the team points, with no title.
    /// </summary>
    public static AtlantisCompletionRewardAward Resolve(
        int teamPoints,
        int admittedMemberCount)
    {
        if (teamPoints >= CompletionTeamPoints)
        {
            return admittedMemberCount switch
            {
                1 => new(Scaled(CompletedHardPoints), DeepSeaHunterTitleId, "Deep Sea Hunter"),
                >= 2 and <= 5 => new(Scaled(CompletedHardPoints), SeabedExplorerTitleId, "Seabed Explorer"),
                _ => throw new ArgumentOutOfRangeException(nameof(admittedMemberCount))
            };
        }

        var hardPoints = IncompleteTiers[0].HardPoints;
        foreach (var tier in IncompleteTiers)
        {
            if (teamPoints < tier.Score)
            {
                break;
            }
            hardPoints = tier.HardPoints;
        }
        return new(Scaled(hardPoints), 0, string.Empty);
    }

    // RATES: Atlantis honour multiplier (public.server_rates). A multiplier of
    // 0 still pays 1, because a settled run must credit a positive amount.
    private static int Scaled(int hardPoints) =>
        Math.Max(
            1,
            Godswar.Server.Application.Rates.ServerRates.Current.Multiply(
                Godswar.Server.Application.Rates.ServerRateKeys.AtlantisHardPoints,
                hardPoints,
                100_000_000));
}

/// <summary>
/// Evidence captured from the completed original instance. The durable entry
/// reservation fences the admitted roster; current connection ownership only
/// controls projection, never entitlement to an already earned reward.
/// </summary>
internal sealed class AtlantisCompletionRewardRequest
{
    public AtlantisCompletionRewardRequest(WorldInstanceId worldInstanceId, RealmId realmId,
        Guid admissionReservationId, DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc,
        int finalScore, IReadOnlyCollection<AtlantisCompletionMember> admittedMembers,
        IReadOnlyCollection<AtlantisCompletionMember> frozenMembers,
        IReadOnlyCollection<Guid>? admissionReservationIds = null)
    {
        ArgumentNullException.ThrowIfNull(admittedMembers);
        ArgumentNullException.ThrowIfNull(frozenMembers);
        var reservations = (admissionReservationIds ?? [admissionReservationId])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Order()
            .ToArray();
        var admitted = admittedMembers.OrderBy(member => member.CharacterId).ToArray();
        var members = frozenMembers.OrderBy(member => member.CharacterId).ToArray();
        if (!worldInstanceId.IsValid || !realmId.IsValid || admissionReservationId == Guid.Empty ||
            reservations.Length == 0 || !reservations.Contains(admissionReservationId) ||
            startedAtUtc == default || startedAtUtc.Offset != TimeSpan.Zero || completedAtUtc.Offset != TimeSpan.Zero ||
            completedAtUtc < startedAtUtc ||
            // A run that reaches its own forty-minute deadline terminalizes exactly
            // on it, so the bound is inclusive: only a run that outlived its limit
            // is invalid evidence.
            completedAtUtc - startedAtUtc > TimeSpan.FromMinutes(40) ||
            finalScore is < 0 or > AtlantisCompletionRewardPolicy.CompletionTeamPoints ||
            !IsValidRoster(admitted) || !IsValidRoster(members))
        {
            throw new ArgumentException("Invalid authoritative Atlantis completion evidence.");
        }
        WorldInstanceId = worldInstanceId;
        RealmId = realmId;
        AdmissionReservationId = admissionReservationId;
        AdmissionReservationIds = Array.AsReadOnly(reservations);
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        FinalScore = finalScore;
        AdmittedMembers = Array.AsReadOnly(admitted);
        AdmittedCharacterIds = Array.AsReadOnly(admitted.Select(member => member.CharacterId).ToArray());
        FrozenMembers = Array.AsReadOnly(members);
        CharacterIds = Array.AsReadOnly(members.Select(member => member.CharacterId).ToArray());
        Award = AtlantisCompletionRewardPolicy.Resolve(finalScore, admitted.Length);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(AtlantisCompletionRewardPolicy.Revision);
            writer.Write(worldInstanceId.Value.ToByteArray());
            writer.Write(realmId.Value);
            writer.Write(admissionReservationId.ToByteArray());
            writer.Write(startedAtUtc.UtcTicks);
            writer.Write(completedAtUtc.UtcTicks);
            writer.Write(finalScore);
            writer.Write(Award.HardPoints);
            writer.Write(Award.TitleId);
            writer.Write(admitted.Length);
            foreach (var member in admitted)
            {
                writer.Write(member.AccountId);
                writer.Write(member.CharacterId);
            }
            writer.Write(members.Length);
            foreach (var member in members)
            {
                writer.Write(member.AccountId);
                writer.Write(member.CharacterId);
            }
            writer.Write(reservations.Length);
            foreach (var reservation in reservations)
            {
                writer.Write(reservation.ToByteArray());
            }
        }
        RequestHash = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    public WorldInstanceId WorldInstanceId { get; }
    public RealmId RealmId { get; }
    public Guid AdmissionReservationId { get; }

    /// <summary>
    /// Every admission reservation that put a member into this run: the leader's
    /// own and, for a member who confirmed the party window after the run was
    /// sealed, that member's own. The ledger check reads them as a set.
    /// </summary>
    public IReadOnlyList<Guid> AdmissionReservationIds { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public TimeSpan Elapsed => CompletedAtUtc - StartedAtUtc;
    public int FinalScore { get; }
    public IReadOnlyList<AtlantisCompletionMember> AdmittedMembers { get; }
    public IReadOnlyList<int> AdmittedCharacterIds { get; }
    public IReadOnlyList<AtlantisCompletionMember> FrozenMembers { get; }
    public IReadOnlyList<int> CharacterIds { get; }
    public AtlantisCompletionRewardAward Award { get; }
    public string RequestHash { get; }

    private static bool IsValidRoster(AtlantisCompletionMember[] members) =>
        members.Length is >= 1 and <= 5 &&
        members.All(member => member.AccountId > 0 && member.CharacterId > 0) &&
        members.Select(member => member.AccountId).Distinct().Count() == members.Length &&
        members.Select(member => member.CharacterId).Distinct().Count() == members.Length;
}

internal enum AtlantisCompletionRewardStatus : byte
{
    Applied = 1,
    Duplicate = 2,
    RequestConflict = 3,
    AdmissionConflict = 4,
    CharacterUnavailable = 5
}

internal readonly record struct AtlantisCompletionRewardMember(int AccountId, int CharacterId,
    byte Camp, int HonorBefore, int HonorAfter, long RewardRevision, uint AwardedTitleId);

internal sealed record AtlantisCompletionRewardReceipt(AtlantisCompletionRewardStatus Status,
    WorldInstanceId WorldInstanceId, AtlantisCompletionRewardAward Award,
    IReadOnlyList<AtlantisCompletionRewardMember> Members)
{
    public bool Succeeded => Status is AtlantisCompletionRewardStatus.Applied or AtlantisCompletionRewardStatus.Duplicate;
}

internal interface IAtlantisCompletionRewardStore
{
    Task<AtlantisCompletionRewardReceipt> SettleAsync(AtlantisCompletionRewardRequest request,
        CancellationToken cancellationToken = default);
}
