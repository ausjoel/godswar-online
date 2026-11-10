using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.State;

/// <summary>One participant's settled reward for one Pindus or Ni Mini round, as the Awarder reads it.</summary>
internal sealed record BattlefieldRewardRecord(
    short Battlefield,
    DateTimeOffset RoundStart,
    DateTimeOffset RoundEnd,
    byte Faction,
    int Level,
    BattlefieldOutcome Outcome,
    long KillPoints,
    long HolyPoints,
    long BossPoints,
    int KillRank,
    int TaskRank,
    int FactionRank,
    BattlefieldReward Reward,
    bool FactionClaimed,
    bool PointsClaimed,
    short TitlesClaimed);

/// <summary>Which part of a round's reward a claim marks.</summary>
internal enum BattlefieldClaimPart
{
    /// <summary>The level package (Pindus 173) or the reputation (Ni Mini 173).</summary>
    Faction = 1,

    /// <summary>The Pindus points package (175).</summary>
    Points = 2
}

/// <summary>A claim that went through: the honour balance after the reputation was added.</summary>
internal sealed record BattlefieldClaimReceipt(int HonorAfter, long RewardRevisionAfter);

/// <summary>The farm's latest round reward for one character (the Awarder's farm page).</summary>
internal sealed record FarmRewardStatus(
    DateTimeOffset RoundStart, FarmRoundOutcome Outcome, int CharacterExperience, int RankOverall, bool Claimed);

/// <summary>The statistics board of the latest round of one battlefield, for one faction.</summary>
internal sealed record BattlefieldStatistics(
    long SpartaPoints,
    long AthensPoints,
    IReadOnlyList<long> TopPoints,
    IReadOnlyList<long> TopKills,
    long OwnPoints);

internal sealed record BattlefieldRoundTick(int Settled, int Rewarded, int TitlesExpired);

/// <summary>What a Holy Water claim or hand-in did, and the refreshed character to install when the bag changed.</summary>
internal enum HolyWaterOutcome
{
    Done = 0,
    AlreadyHolding = 1,
    BagFull = 2,
    NoneHeld = 3,

    /// <summary>The round settled (or is not open): nothing changed.</summary>
    RoundClosed = 4,

    /// <summary>The bag holds a Holy Water, but none was issued to the character in this round still unspent.</summary>
    NotIssuedThisRound = 5
}

internal sealed record HolyWaterChange(HolyWaterOutcome Outcome, GameCharacter? Character, int Points = 0);

/// <summary>
/// Pindus / Ni Mini rounds, scores and Awarder claims. Every member does nothing by default so a profile without
/// durable state (the in-memory store) never scores or pays.
/// </summary>
internal interface IBattlefieldRoundStore
{
    /// <summary>Records a participant of an open round (creating the round row on first use).</summary>
    Task RecordBattlefieldParticipantAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int characterId, byte faction,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Credits one enemy kill: both players become participants, the killer gains <paramref name="personalPoints"/>
    /// and the killer's faction <paramref name="factionPoints"/>. Returns false when the round is already settled.
    /// </summary>
    Task<bool> RecordBattlefieldKillAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int killerId, byte killerFaction,
        int victimId, byte victimFaction, int victimLevel, int factionPoints, int personalPoints,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// Adds non-kill points (Holy Water, the Pindus Dragon) to a participant and the same amount to their faction.
    /// Returns false when the round is already settled.
    /// </summary>
    Task<bool> RecordBattlefieldTaskPointsAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int characterId, byte faction,
        int holyPoints, int bossPoints, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>Settles every finished round and expires battlefield titles older than <paramref name="titleDays"/>.</summary>
    Task<BattlefieldRoundTick> AdvanceBattlefieldRoundsAsync(
        DateTimeOffset now, int titleDays, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BattlefieldRoundTick(0, 0, 0));

    /// <summary>
    /// The character's settled rewards on this battlefield for rounds that started at or after
    /// <paramref name="since"/>, oldest first (the Awarder claims the oldest claimable one, so a second round on the
    /// same day never hides the first).
    /// </summary>
    Task<IReadOnlyList<BattlefieldRewardRecord>> GetBattlefieldRewardsAsync(
        short battlefield, int characterId, DateTimeOffset since, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BattlefieldRewardRecord>>([]);

    /// <summary>True when the character is a participant of a round that has not been settled yet.</summary>
    Task<bool> IsInUnsettledBattlefieldRoundAsync(
        short battlefield, int characterId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Marks one part claimed and adds <paramref name="reputation"/> to the honour balance in one transaction.
    /// Null when that part was already claimed.
    /// </summary>
    Task<BattlefieldClaimReceipt?> ClaimBattlefieldRewardAsync(
        short battlefield, DateTimeOffset roundStart, int characterId, BattlefieldClaimPart part, int reputation,
        CancellationToken cancellationToken = default) => Task.FromResult<BattlefieldClaimReceipt?>(null);

    /// <summary>
    /// Undoes a claim whose EXP / talent payment failed: clears the part's claim mark and takes back the reputation
    /// it added, in one transaction, so a later click pays the whole part again. Null when the part was not claimed.
    /// </summary>
    Task<BattlefieldClaimReceipt?> UnclaimBattlefieldRewardAsync(
        short battlefield, DateTimeOffset roundStart, int characterId, BattlefieldClaimPart part, int reputation,
        CancellationToken cancellationToken = default) => Task.FromResult<BattlefieldClaimReceipt?>(null);

    /// <summary>
    /// Marks a title flag claimed and grants the title (granted_by <paramref name="grantedBy"/>). False when the flag
    /// was already set.
    /// </summary>
    Task<bool> ClaimBattlefieldTitleAsync(
        short battlefield, DateTimeOffset roundStart, int characterId, short flag, int titleId, string grantedBy,
        string note, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>The latest (open or settled) round's board for one faction, or null when there is no round.</summary>
    Task<BattlefieldStatistics?> GetBattlefieldStatisticsAsync(
        short battlefield, byte faction, int characterId, CancellationToken cancellationToken = default) =>
        Task.FromResult<BattlefieldStatistics?>(null);

    /// <summary>
    /// Issues one Holy Water for the open round (IGG: one at a time). Refused while this round's bottle is still out;
    /// a bottle already in the bag from an earlier round is adopted as this round's bottle instead of adding another.
    /// </summary>
    Task<HolyWaterChange> GrantHolyWaterAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int characterId, byte faction,
        int itemId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new HolyWaterChange(HolyWaterOutcome.BagFull, null));

    /// <summary>
    /// Hands one Holy Water in at an altar: in ONE transaction the round row is locked (refused once settled), a
    /// bottle issued this round is required, one 4522 leaves the bag and the points
    /// (<see cref="Application.WorldInstances.BattlefieldRewardPolicy.HolyWaterPoints"/>) go to the player and faction.
    /// </summary>
    Task<HolyWaterChange> TurnInHolyWaterAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int characterId, byte faction,
        bool hostileAltar, int itemId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new HolyWaterChange(HolyWaterOutcome.NoneHeld, null));

    /// <summary>The character's latest Lelantine Farm round reward, or null.</summary>
    Task<FarmRewardStatus?> GetLatestFarmRewardAsync(
        int characterId, CancellationToken cancellationToken = default) => Task.FromResult<FarmRewardStatus?>(null);
}
