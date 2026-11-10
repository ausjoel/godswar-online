using Godswar.Server.Application.Guilds;

namespace Godswar.Server.Infrastructure.Guilds;

internal sealed partial class PostgresGuildStore
{
    /// <summary>
    /// The client's altar content, for callers that need to recompute a bonus from
    /// balances they already hold.
    /// </summary>
    public IGuildAltarContent AltarContent => _altarContent;

    /// <summary>
    /// The total attribute bonus the altars give one character.
    /// </summary>
    /// <remarks>
    /// Both layers are resolved together from a single read of the member's
    /// offering rows: the member's own points, and the altar levels the guild has
    /// built, which pay every member alike. Content comes from
    /// <see cref="PostgresGuildAltarContent"/>, which holds the client's own
    /// <c>WorshipImpactType</c>/<c>WorshipImpactValue</c> rows.
    /// </remarks>
    public GuildAltarAttributeBonus ResolveAltarBonus(
        IReadOnlyList<GuildAltarWorshipBalance> balances) =>
        GuildAltarWorshipBonusPolicy.Resolve(
            balances,
            _altarContent.ContentOf);
}
