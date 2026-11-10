using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.State;

/// <summary>One unclaimed round reward, as read back for a character.</summary>
internal sealed record FarmRoundRewardClaim(
    DateTimeOffset RoundStart,
    FarmRoundOutcome Outcome,
    int PetExperience,
    int CharacterExperience,
    int TalentPoints);

/// <summary>The wooden tools just handed out and the refreshed character.</summary>
internal sealed record FarmToolGrant(int Granted, GameCharacter? Character);

/// <summary>What one round clock tick did (for the log).</summary>
internal sealed record FarmRoundTick(int Started, int Settled, int Rewarded);

/// <summary>
/// The farm's round clock, its settlement and the rewards each scorer is owed. Every member refuses by default so a
/// profile without durable state does nothing.
/// </summary>
internal interface ILelantineFarmRoundStore
{
    /// <summary>
    /// Starts the current Saturday window (archiving and clearing the scores) and settles every finished,
    /// unsettled one, in one transaction. Safe to call repeatedly and after a restart.
    /// </summary>
    Task<FarmRoundTick> AdvanceFarmRoundsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FarmRoundTick(0, 0, 0));

    /// <summary>Marks the character's unclaimed rewards claimed and returns them.</summary>
    Task<IReadOnlyList<FarmRoundRewardClaim>> ClaimFarmRoundRewardsAsync(
        int characterId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FarmRoundRewardClaim>>([]);

    /// <summary>Puts a reward back as unclaimed when paying it failed.</summary>
    Task UnclaimFarmRoundRewardAsync(
        int characterId, DateTimeOffset roundStart, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Hands out the free wooden tools the character is owed on the farm (see <see cref="LelantineFarmToolPolicy"/>).
    /// The returned character is the refreshed row to install, or null when nothing was granted.
    /// </summary>
    Task<FarmToolGrant> GrantFarmToolsAsync(
        int characterId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FarmToolGrant(0, null));

    /// <summary>Grants a title through the administrative grant table (idempotent).</summary>
    Task GrantFarmTitleAsync(
        int characterId, int titleId, string note, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
