using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.State;

/// <summary>What one hound-egg donation did.</summary>
internal enum FarmDonationStatus
{
    /// <summary>The eggs were consumed and the faction total advanced.</summary>
    Donated,

    /// <summary>The character row no longer exists.</summary>
    CharacterNotFound,

    /// <summary>The character carries fewer eggs than the request asked for.</summary>
    NotEnoughEggs,

    /// <summary>
    /// The item control the donation was submitted from does not hold a hound
    /// egg, so there was nothing to take.
    /// </summary>
    NoEggInBox,

    /// <summary>
    /// The running profile has no durable score ledger, so nothing was written.
    /// A score this server cannot store is refused rather than invented.
    /// </summary>
    Unsupported
}

/// <summary>The committed result of one donation.</summary>
internal sealed record FarmDonationResult(
    FarmDonationStatus Status,
    GameCharacter? Character,
    int EggsDonated,
    int PointsAwarded,
    long FactionPoints,
    long CharacterDonatedPoints)
{
    public bool Donated => Status == FarmDonationStatus.Donated;
}

/// <summary>
/// The farm's two score ledgers. They live apart from <c>IGameStore</c> because
/// they are the activity's own bookkeeping rather than character progression:
/// the faction row is mutated by donation alone, and the kill row by monster
/// deaths alone.
/// </summary>
/// <remarks>
/// Every member carries a refusing default so a profile without durable
/// gameplay state can answer the activity without pretending a score was
/// recorded.
/// </remarks>
internal interface ILelantineFarmPointsStore
{
    /// <summary>
    /// Consumes <paramref name="eggCount"/> hound eggs of
    /// <paramref name="itemId"/> from the character's kit bag and advances their
    /// faction's donated total, in one transaction.
    /// </summary>
    /// <param name="bagSlot">
    /// The kit-bag slot the player placed the egg in, which is the stack the
    /// donation is taken from, or <see langword="null"/> for the activity's
    /// "donate every hound egg you carry" button, which takes the eggs in slot
    /// order.
    /// </param>
    Task<FarmDonationResult> DonateHoundEggsAsync(
        int accountId,
        int characterId,
        byte faction,
        int itemId,
        int? bagSlot,
        int eggCount,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new FarmDonationResult(
            FarmDonationStatus.Unsupported,
            Character: null,
            EggsDonated: 0,
            PointsAwarded: 0,
            FactionPoints: 0,
            CharacterDonatedPoints: 0));

    /// <summary>
    /// Adds <paramref name="points"/> to a character's personal farm score for
    /// one credited kill. A score row is created on the first credit.
    /// </summary>
    Task CreditFarmKillAsync(
        int characterId,
        byte faction,
        int points,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Reads the activity's score board: both camps' totals and the asking
    /// character's own standing in it.
    /// </summary>
    Task<FarmScoreSnapshot?> ReadFarmScoresAsync(
        int characterId,
        byte faction,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<FarmScoreSnapshot?>(null);
}

/// <summary>
/// The farm's score board as one read: the two camps' totals, and the asking
/// character's own score and rank.
/// </summary>
/// <remarks>
/// Every figure is a sum over the same per-character ledger, so the camps'
/// totals are the sums of their members' personal scores and the rank is taken
/// over the same scores the highest one is.
/// </remarks>
internal sealed record FarmScoreSnapshot(
    byte Faction,
    long CharacterDonatedPoints,
    long CharacterKillPoints,
    long CharacterPoints,
    long HighestPoints,
    long CharacterRank,
    long SpartaPoints,
    long AthensPoints)
{
    /// <summary>The asking character's own camp's total.</summary>
    public long FactionPoints => Faction == LelantineFarmPointsPolicy.SpartaFaction
        ? SpartaPoints
        : AthensPoints;

    /// <summary>The other camp's total.</summary>
    public long OtherFactionPoints =>
        Faction == LelantineFarmPointsPolicy.SpartaFaction
            ? AthensPoints
            : SpartaPoints;
}
