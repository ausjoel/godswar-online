using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Rates;
using Godswar.Server.State;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// BAY2: the Bay Under Attack completion reward. Every member still inside
/// when the run ends (Christina's kill, the leader's end vote or the 30-minute
/// timer) is paid fighter EXP scaled by the run's CLEARANCE RATE, once.
/// </summary>
/// <remarks>
/// <para>
/// Retail (IGG guide acid=201): the leaders "promised large rewards", and a
/// party that ends the run takes "a certain amount of rewards depending on the
/// team's progression". The guide gives NO amounts.
/// </para>
/// <para>
/// GENERATED (the admin 2026-11-03, round 2 answer 2: "EXP rewards scaled by
/// clearance rate"):
/// clearance = team score cleared / team score the run's map loaded (1 normal,
/// 10 elite, 50 boss, the same generated weights the panel shows), frozen when
/// the run first ends; EXP = clearance x <c>instance.harbor_attack_exp_pct</c>
/// (default 100) percent of the EXP the member's own level needs for the next
/// level (<see cref="PlayerExperienceCatalog.GetNextLevelExperience"/>). So a
/// full clear is worth one level at the member's level, half a clear half a
/// level, and so on. The global exp.* multipliers do not apply; the percent
/// row is this reward's own slider.
/// </para>
/// <para>
/// Level 200 earns 0 EXP (the server's no-EXP cap, as for monster kills); the
/// receipt is still written so the settlement is never retried.
/// </para>
/// </remarks>
internal static class HarborAttackCompletionRewardPolicy
{
    public const string Revision = "harbor-completion-v1";

    /// <summary>GENERATED default: a full clear pays 100% of one level.</summary>
    public const int DefaultPercentOfLevel = 100;

    public const int MaximumPercentOfLevel = 1_000;

    /// <summary>Clearance is held in basis points (10,000 = everything cleared).</summary>
    public const int FullClearanceBasisPoints = 10_000;

    public static int ClearanceBasisPoints(int clearedScore, int totalScore)
    {
        if (totalScore <= 0 || clearedScore <= 0)
        {
            return 0;
        }
        return (int)Math.Min(
            FullClearanceBasisPoints,
            (long)clearedScore * FullClearanceBasisPoints / totalScore);
    }

    /// <summary>The live slider (0-1,000 percent of one level at full clearance).</summary>
    public static int CurrentPercentOfLevel =>
        (int)Math.Clamp(
            ServerRates.Current.Absolute(ServerRateKeys.HarborAttackExperiencePercent),
            0,
            MaximumPercentOfLevel);

    /// <summary>
    /// EXP one member earns: 0 at level 200 or with no clearance,
    /// otherwise clearance x percent x next-level EXP, at least 1.
    /// </summary>
    public static int Resolve(
        int level,
        bool fighterLevelSealed,
        int clearanceBasisPoints,
        int percentOfLevel)
    {
        // Level 200 earns nothing, sealed or not (the monster-kill rule,
        // MonsterRewardCatalog.Resolve).
        if (level is < 1 or >= PlayerExperienceCatalog.MaximumLevel ||
            clearanceBasisPoints <= 0 ||
            percentOfLevel <= 0)
        {
            return 0;
        }
        var clearance = Math.Min(clearanceBasisPoints, FullClearanceBasisPoints);
        var percent = Math.Min(percentOfLevel, MaximumPercentOfLevel);
        var experience = (long)PlayerExperienceCatalog.GetNextLevelExperience(level) *
            clearance * percent / (FullClearanceBasisPoints * 100L);
        return (int)Math.Clamp(experience, 1, int.MaxValue);
    }
}

internal readonly record struct HarborAttackCompletionMember(int AccountId, int CharacterId);

/// <summary>
/// The frozen evidence of one ended Bay Under Attack run. Built once, when the
/// run is first seen ended; a retry re-sends the same object, so its hash
/// never changes.
/// </summary>
internal sealed class HarborAttackCompletionRewardRequest
{
    public HarborAttackCompletionRewardRequest(
        Guid runReservationId,
        Guid worldInstanceId,
        short realmId,
        byte mapId,
        string endReason,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        int clearedScore,
        int totalScore,
        int percentOfLevel,
        IReadOnlyCollection<HarborAttackCompletionMember> members,
        IReadOnlyCollection<Guid> entryReservationIds)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(entryReservationIds);
        var roster = members.OrderBy(static member => member.CharacterId).ToArray();
        var reservations = entryReservationIds.Append(runReservationId)
            .Where(static id => id != Guid.Empty).Distinct().Order().ToArray();
        if (runReservationId == Guid.Empty || worldInstanceId == Guid.Empty || realmId <= 0 ||
            mapId is not (208 or 209) ||
            endReason is not ("completed" or "terminated" or "deadline") ||
            startedAtUtc.Offset != TimeSpan.Zero || endedAtUtc.Offset != TimeSpan.Zero ||
            endedAtUtc < startedAtUtc ||
            clearedScore < 0 || totalScore < 0 ||
            percentOfLevel is < 0 or > HarborAttackCompletionRewardPolicy.MaximumPercentOfLevel ||
            roster.Length is < 1 or > 5 ||
            roster.Any(static member => member.AccountId <= 0 || member.CharacterId <= 0) ||
            roster.Select(static member => member.CharacterId).Distinct().Count() != roster.Length ||
            roster.Select(static member => member.AccountId).Distinct().Count() != roster.Length)
        {
            throw new ArgumentException("Invalid Bay Under Attack completion evidence.");
        }

        RunReservationId = runReservationId;
        WorldInstanceId = worldInstanceId;
        RealmId = realmId;
        MapId = mapId;
        EndReason = endReason;
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        ClearedScore = clearedScore;
        TotalScore = totalScore;
        ClearanceBasisPoints = HarborAttackCompletionRewardPolicy.ClearanceBasisPoints(
            clearedScore, totalScore);
        PercentOfLevel = percentOfLevel;
        Members = Array.AsReadOnly(roster);
        CharacterIds = Array.AsReadOnly(roster.Select(static member => member.CharacterId).ToArray());
        EntryReservationIds = Array.AsReadOnly(reservations);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(HarborAttackCompletionRewardPolicy.Revision);
            writer.Write(runReservationId.ToByteArray());
            writer.Write(worldInstanceId.ToByteArray());
            writer.Write(realmId);
            writer.Write(mapId);
            writer.Write(endReason);
            writer.Write(startedAtUtc.UtcTicks);
            writer.Write(endedAtUtc.UtcTicks);
            writer.Write(clearedScore);
            writer.Write(totalScore);
            writer.Write(percentOfLevel);
            writer.Write(roster.Length);
            foreach (var member in roster)
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

    public Guid RunReservationId { get; }
    public Guid WorldInstanceId { get; }
    public short RealmId { get; }
    public byte MapId { get; }
    public string EndReason { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset EndedAtUtc { get; }
    public int ClearedScore { get; }
    public int TotalScore { get; }
    public int ClearanceBasisPoints { get; }
    public int PercentOfLevel { get; }
    public IReadOnlyList<HarborAttackCompletionMember> Members { get; }
    public IReadOnlyList<int> CharacterIds { get; }

    /// <summary>The run's own reservation plus each late joiner's own.</summary>
    public IReadOnlyList<Guid> EntryReservationIds { get; }
    public string RequestHash { get; }
}

internal enum HarborAttackCompletionRewardStatus : byte
{
    Applied = 1,
    Duplicate = 2,
    RequestConflict = 3
}

/// <summary>
/// One member's receipt. <see cref="Admitted"/> is false for a character who
/// holds no Bay Under Attack daily entry for this run (paid 0).
/// </summary>
internal readonly record struct HarborAttackCompletionRewardMember(
    int AccountId,
    int CharacterId,
    bool Admitted,
    int ExperienceGranted,
    int LevelBefore,
    long ExperienceBefore,
    int LevelAfter,
    long ExperienceAfter,
    long ProgressionRevision);

internal sealed record HarborAttackCompletionRewardReceipt(
    HarborAttackCompletionRewardStatus Status,
    Guid RunReservationId,
    int ClearanceBasisPoints,
    IReadOnlyList<HarborAttackCompletionRewardMember> Members)
{
    public bool Succeeded => Status is HarborAttackCompletionRewardStatus.Applied or
        HarborAttackCompletionRewardStatus.Duplicate;
}

internal interface IHarborAttackCompletionRewardStore
{
    /// <summary>
    /// Pays every member once, in ONE transaction, and closes the run's ledger
    /// row in the same transaction (so a restart refund can never give back the
    /// entry of a paid run). A second call with the same request returns the
    /// stored receipt (Duplicate) and pays nothing.
    /// </summary>
    Task<HarborAttackCompletionRewardReceipt> SettleAsync(
        HarborAttackCompletionRewardRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>The character's stored fighter level and EXP (for projection).</summary>
    Task<(int Level, long Experience)?> ReadProgressionAsync(
        int characterId,
        CancellationToken cancellationToken = default);
}
