namespace Godswar.Server.Application.Guilds;

/// <summary>
/// One guild as the guild window's List tab renders it.
/// </summary>
/// <remarks>
/// The tab shows every guild of the viewer's own camp with its member numbers and
/// its footstone, so the entry carries what the row needs: the guild's name, the
/// lord's name, the counts, and the footstone (building type 1) level. The camp
/// itself is not a column of <c>guilds</c> - a guild belongs to the camp of its
/// lord - so the reader resolves it through <c>character_base.camp</c>.
/// </remarks>
internal sealed record GuildListEntry(
    long GuildId,
    string Name,
    string LordName,
    byte Level,
    int MemberCount,
    int MemberLimit,
    int OnlineCount,
    long Silver,
    long Gold,
    int Bijou,
    int FootstoneLevel,
    int BuildingCount,
    bool RefusesApplications);
