using Godswar.Server.Application.Guilds;
using Godswar.Server.Infrastructure.Guilds;

namespace Godswar.Server.Game;

/// <summary>
/// The hourly altar drain, as the background settlement needs it.
/// </summary>
/// <remarks>
/// Declared here rather than in the guild store so the session registry depends on
/// the capability and not on the PostgreSQL type.
/// </remarks>
internal interface GuildAltarSettlementStore
{
    /// <summary>
    /// Drains the altars of the given characters up to <paramref name="now"/>.
    /// </summary>
    /// <returns>
    /// Only the altars whose balance actually moved, so the caller can refresh
    /// exactly those members.
    /// </returns>
    Task<IReadOnlyList<GuildAltarWorshipSettlement>> TrySettleAltarWorshipAsync(
        IReadOnlyList<int> characterIds,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// The balances a member's altars hold, which is what the bonus is recomputed
    /// from after a settlement moved them.
    /// </summary>
    Task<IReadOnlyList<GuildAltarWorshipBalance>> TryReadAltarWorshipAsync(
        int characterId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// The client's altar content, so a caller can recompute a member's bonus from
    /// the balances the settlement left behind.
    /// </summary>
    IGuildAltarContent AltarContent { get; }
}
