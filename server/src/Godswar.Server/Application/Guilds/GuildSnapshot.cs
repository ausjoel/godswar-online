namespace Godswar.Server.Application.Guilds;

/// <summary>
/// One guild as the client's own guild window expects it.
/// </summary>
/// <remarks>
/// The field set is the one the stock client renders: the window prints
/// <c>ConsortiaLevel</c>, <c>MemberMax</c>, <c>ConsortiaFunds</c>,
/// <c>ConsortiaBijou</c> and a member table, and every one of those reads a
/// field of the message this repository fills in
/// (<c>PacketBuilder.GuildBaseInfo</c>).
/// </remarks>
internal sealed record GuildSnapshot(
    long GuildId,
    string Name,
    byte Level,
    int MemberLimit,
    long Silver,
    long Gold,
    int Bijou,
    string Proclamation,
    IReadOnlyList<GuildMemberSnapshot> Members,
    IReadOnlyList<GuildBuildingSnapshot> Buildings);

/// <summary>One row of the guild window's member table.</summary>
internal sealed record GuildMemberSnapshot(
    int CharacterId,
    string Name,
    short Level,
    byte Duty,
    byte Profession,
    bool Online = false,
    long ContributionGold = 0,
    long ContributionSilver = 0);

/// <summary>
/// One entry of the guild's building array.
/// </summary>
/// <remarks>
/// The client draws these from the base info's array at <c>+0x168</c>, which is
/// <em>not</em> the member table: writing member records there makes the window
/// list them as buildings (measured 2026-09-21, recorded in
/// <c>docs/工会系统技术文档.md</c> §4.1.1).
/// </remarks>
internal sealed record GuildBuildingSnapshot(int BuildingType, byte Level);
