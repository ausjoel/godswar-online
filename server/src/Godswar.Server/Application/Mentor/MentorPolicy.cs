namespace Godswar.Server.Application.Mentor;

/// <summary>
/// ECONOMY mentor (2026-11-03): the retail Master and Apprentice rules, IGG guide "The Master and Apprentice System"
/// (Development/reference-analysis/text/igg-acid=111.txt).
/// </summary>
/// <remarks>
/// Apprentice: below level 49, at least 30 levels under the master, no master yet, not in a 24-hour lockout.
/// Master: level 50+, at least 30 levels above, at most 6 apprentices, not in a 24-hour lockout. Both must be in the
/// same team (the guide's procedure). Leaving (apprentice "Escape") or expelling (master "Expel") ends the pair and
/// locks BOTH out for 24 hours. At level 50 the apprentice graduates: apprentice 200,000 EXP, 50 Talent Points,
/// 20,000 Silver, 2,000 B-Gold; master 100,000 EXP, 20 Talent Points, 10,000 Silver (all from the guide).
/// NOT BUILT (listed in the README): the master's 200 Reputation (this server keeps no Reputation balance), Masters
/// Ethics (per level-up while teamed, its deduction on expel, and the exchange), the apprentice and master titles.
/// Off unless GODSWAR_MENTOR=1: with the switch off, message 10234 keeps today's "[game] unknown" path.
/// </remarks>
internal static class MentorPolicy
{
    public const int ApprenticeLevelBelow = 49;
    public const int MasterMinimumLevel = 50;
    public const int MinimumLevelGap = 30;
    public const int MaximumApprentices = 6;
    public const int GraduationLevel = 50;
    public static readonly TimeSpan Lockout = TimeSpan.FromHours(24);
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromSeconds(60);

    public static readonly MentorRewardValues ApprenticeReward = new(200_000, 50, 20_000, 2_000);
    // STAGE3 (the admin 2026-11-04): the master is paid in B-Gold for teaching. The guide's Masters Ethics (per teamed
    // level-up, exchanged for B-Gold; GodsArena table 8,000/25,000/70,000 Ethics -> 1,380/2,300/3,220 B-Gold) has no
    // numbers, so B-Gold is paid directly: MasterLevelBindingGold for each level an apprentice gains while in the
    // relationship (levels start+1 .. 49, once per apprentice and level, ever), and 2,000 B-Gold at graduation (the
    // apprentice's own guide figure). A Lv1 apprentice taught to 50 pays the master 48 x 20 + 2,000 = 2,960 B-Gold.
    public const int MasterLevelBindingGold = 20;

    // W2 F-23 (the admin: "Build Masters Ethics instead"): with mentor.masters_ethics on, each level an apprentice gains pays
    // the master EthicsPerLevel x the level reached in Masters Ethics instead of MasterLevelBindingGold (GENERATED: a Lv1
    // apprentice taught to 49 is 12,240 Ethics, one 8,000 exchange). Ethics are spent at the Exchange Mentor (client
    // TS311-313: 8,000 / 25,000 / 70,000 -> 1,380 / 2,300 / 3,220 B-Gold, once per month). No team needed (Stage 3 rule).
    public const int EthicsPerLevel = 10;
    public const string MastersEthicsRateKey = "mentor.masters_ethics";
    public static bool MastersEthicsOn =>
        Godswar.Server.Application.Rates.ServerRates.Current.Get(MastersEthicsRateKey) >= 1m;
    public static readonly MentorRewardValues MasterReward = new(100_000, 20, 10_000, 2_000);

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("GODSWAR_MENTOR") == "1";

    /// <summary>Whether this pair may start a relationship, before the database's own checks.</summary>
    public static bool LevelsAllow(int masterLevel, int apprenticeLevel) =>
        masterLevel >= MasterMinimumLevel &&
        apprenticeLevel < ApprenticeLevelBelow &&
        masterLevel - apprenticeLevel >= MinimumLevelGap;
}

internal readonly record struct MentorRewardValues(long Experience, int TalentPoints, int Silver, int BindingGold);

internal sealed record MentorMember(int CharacterId, string Name, int Level, int MapId, bool IsMaster);

internal sealed record MentorView(
    int CharacterId,
    int? MasterId,
    MentorMember? Master,
    IReadOnlyList<MentorMember> Apprentices,
    DateTimeOffset? LockedUntil)
{
    public bool HasRelationship => Master is not null;
}

internal enum MentorCreateStatus
{
    Created,
    AlreadyHasMaster,
    MasterFull,
    Locked,
    Ineligible
}

internal sealed record MentorReward(long Id, bool AsMaster, string PartnerName, long Experience, int TalentPoints, int Silver,
    int BindingGold, int AwardLevel = 0)
{
    /// <summary>STAGE3: a per-level master award (B-Gold only), not a graduation reward.</summary>
    public bool IsLevelAward => AwardLevel > 0;
}

internal sealed record MentorClaim(IReadOnlyList<MentorReward> Rewards, int SilverAfter, int BindingGoldAfter, int TalentPointsAfter);

internal interface IMentorStore
{
    Task<MentorView> ReadAsync(int characterId, CancellationToken cancellationToken = default);
    Task<MentorCreateStatus> CreateAsync(int masterId, int apprenticeId, CancellationToken cancellationToken = default);
    Task<int?> EndAsync(int apprenticeId, int? requiredMasterId, CancellationToken cancellationToken = default);
    Task<int?> GraduateAsync(int apprenticeId, CancellationToken cancellationToken = default);
    /// <summary>STAGE3: owes the master one award per level the apprentice gained in the relationship; returns the
    /// master id when a new award was recorded.</summary>
    Task<int?> RecordLevelAwardsAsync(int apprenticeId, CancellationToken cancellationToken = default);
    Task<MentorClaim?> ClaimRewardsAsync(int characterId, int accountId, CancellationToken cancellationToken = default);
}
