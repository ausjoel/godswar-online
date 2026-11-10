using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Guilds;

// GUILD (2026-11-03): guild rules from the IGG guide (igg-acid=67 "Guild
// Introduction"). Every rule here is behind a server_rates switch in the
// "Guild" category, so each one can be turned off on the live server without a
// restart (0 = exactly the behaviour before this patch).

/// <summary>The server_rates keys of the guild rules.</summary>
internal static class GuildRateKeys
{
    public const string BuildingRules = "guild.building_rules";
    public const string BuildDuty = "guild.build_duty";
    public const string DeleteDuty = "guild.delete_duty";
    public const string AltarCurve = "guild.altar_curve";
    public const string LordRules = "guild.lord_rules";
    public const string DoubleClaims = "guild.double_claims";
    public const string DoubleLevel = "guild.double_level";
    public const string QuestRules = "guild.quest_rules";
    public const string QuestReleaseDuty = "guild.quest_release_duty";

    public const string Category = "Guild";

    /// <summary>
    /// The rows the rates tab shows. Spread into
    /// <see cref="ServerRateCatalog.Definitions"/> by one line.
    /// </summary>
    public static IEnumerable<ServerRateDefinition> Definitions { get; } =
    [
        Switch(
            BuildingRules, 10, "Guild building rules",
            "Infrastructure/Guilds/PostgresGuildStore.BuildingRules.cs",
            "1 = building, upgrading and demolishing a guild building follow " +
            "the IGG guide: each level costs the guild purse the Gold and " +
            "Silver of the guide's table (Consortia.xml), no building may go " +
            "above the Guild Footstone's level (the guild level follows the " +
            "Footstone), and only the ranks below may build or demolish. " +
            "0 = free and open to every member, as before."),
        Duty(
            BuildDuty, 20, "Guild rank that may build/upgrade", 5m,
            "Infrastructure/Guilds/PostgresGuildStore.BuildingRules.cs",
            "Lowest guild duty that may build or upgrade a guild building: " +
            "6 Lord, 5 Deputy, 4 Director, 3 Elite, 2 Insider, 1 Member. " +
            "Default 5 (Lord and Deputies)."),
        Duty(
            DeleteDuty, 30, "Guild rank that may demolish", 6m,
            "Infrastructure/Guilds/PostgresGuildStore.BuildingRules.cs",
            "Lowest guild duty that may demolish a guild building. Default 6 " +
            "(Lord only). The Footstone can never be demolished."),
        Switch(
            AltarCurve, 40, "Altar offering bonus from the first point",
            "Application/Guilds/GuildAltarWorshipBonus.cs",
            "1 = the IGG guide's offering curve: every 1,000 points held on " +
            "an altar pays +1% of its base value, up to 150% at 150,000 " +
            "points. 0 = the old curve (nothing below 50,000, then 7.5% per " +
            "50,000 up to 150% at 1,000,000)."),
        Switch(
            LordRules, 50, "Guild Lord hand-over",
            "Game/GameClientHandler.GuildMembers.cs, GuildExit.cs",
            "1 = the Lord can pass the lordship to a member (the guild " +
            "window's Change Lord button) and cannot leave the guild while " +
            "holding it. 0 = as before (no hand-over; the Lord may leave)."),
        Switch(
            DoubleClaims, 60, "Member Advisor double EXP/TP claims",
            "Game/GameClientHandler.GuildWelfare.cs",
            "1 = guild members claim double EXP and double TP hours from the " +
            "[Guild] Member Advisor out of a weekly allowance (resets on " +
            "Saturday). 0 = the advisor only answers 'insufficient time', " +
            "as before."),
        new ServerRateDefinition(
            DoubleLevel, Category,
            "Guild double EXP/TP level (hours per week = level + 2)",
            "level", ServerRateKind.Absolute, 2m, 0m, 22m, 1m, 70,
            "Game/GameClientHandler.GuildWelfare.cs",
            "The double EXP and double TP level every guild has. The client " +
            "text says the weekly hours are the level plus two and the level " +
            "goes up to 22. Retail let the Lord buy levels with guild funds; " +
            "those prices are not in the guide, so the level is set here for " +
            "every guild. Default 2 = 4 hours a week of each (the guide's " +
            "1-4 hour claims)."),
        Switch(
            QuestRules, 80, "Guild quests",
            "Game/GameClientHandler.GuildWelfare.cs, GameClientHandler.Quests.cs",
            "1 = the guild quests of the [Guild] Quest Supervisor (Sparta_039 " +
            "/ Athens_038) must be released for the day by an officer, need " +
            "a guild, and pay their Guild Gold and Guild Fund to the guild " +
            "and their Contribution to the member, as the client's quest " +
            "text lists. 0 = as before (anyone may take them and the 'Guild " +
            "Gold' is paid to the player as personal gold). Default 0 " +
            "(review fix): turn it on only after the Guild Stone (economy " +
            "r09) is live and guilds exist, or every player loses these " +
            "dailies.",
            defaultValue: 0m),
        Duty(
            QuestReleaseDuty, 90, "Guild rank that may release guild quests", 4m,
            "Game/GameClientHandler.GuildWelfare.cs",
            "Lowest guild duty that may release the day's guild quests. " +
            "Default 4 (Director; the guide gives Lord, Deputy and Director " +
            "the right).")
    ];

    public static bool On(string key) => ServerRates.Current.Absolute(key) != 0;

    public static int DutyOf(string key) =>
        (int)Math.Clamp(ServerRates.Current.Absolute(key), 1, 6);

    private static ServerRateDefinition Switch(
        string key,
        int sortOrder,
        string label,
        string appliesTo,
        string description,
        decimal defaultValue = 0m) => // RELEASE: every guild switch defaults off
        new(key, Category, label, "0/1", ServerRateKind.Absolute, defaultValue,
            0m, 1m, 1m, sortOrder, appliesTo, description);

    private static ServerRateDefinition Duty(
        string key,
        int sortOrder,
        string label,
        decimal defaultValue,
        string appliesTo,
        string description) =>
        new(key, Category, label, "duty", ServerRateKind.Absolute,
            defaultValue, 1m, 6m, 1m, sortOrder, appliesTo, description);
}

/// <summary>What a building action did, as the altar reports it.</summary>
internal enum GuildBuildingOutcome
{
    Done,
    NotAMember,
    NoAuthority,
    FootstoneTooLow,
    TopRank,
    NoSuchBuilding,
    AlreadyBuilt,
    ShortOfGold,
    ShortOfSilver,
    FootstoneProtected,
    UnknownBuilding
}

/// <summary>
/// The pure part of the guide's building rules, kept apart from the SQL so the
/// checks can test it.
/// </summary>
internal static class GuildBuildingPolicy
{
    /// <summary>The Guild Footstone's building type (Consortia.xml BuildInfo 1).</summary>
    public const int FootstoneType = 1;

    /// <summary>The Lord's duty number (Consortia_Job.ini 6).</summary>
    public const int LordDuty = 6;

    /// <summary>
    /// The level a building is going to after the action, or 0 for a demolition.
    /// </summary>
    public static int TargetLevel(bool build, int currentLevel) =>
        build ? 1 : currentLevel + 1;

    /// <summary>
    /// The guide: "the guild level must be in accordance with the footstone
    /// level, so before upgrading other guild buildings, the footstone must be
    /// first upgraded". A guild without a Footstone row stands at its own level
    /// (1 for every guild the server creates), which is the Footstone's free
    /// first level.
    /// </summary>
    public static int EffectiveFootstoneLevel(int? footstoneRowLevel, int guildLevel) =>
        Math.Max(Math.Max(footstoneRowLevel ?? 0, guildLevel), 1);

    /// <summary>
    /// Decides a build or upgrade once the guild's numbers are known.
    /// </summary>
    /// <param name="cost">
    /// The guide's cost of the target level: (silver = upgrade_found,
    /// gold = upgrade_bijou) of <c>guild_building_levels</c>, or null when the
    /// type has no such level (its top rank).
    /// </param>
    public static GuildBuildingOutcome DecideRaise(
        int actorDuty,
        int requiredDuty,
        int buildingType,
        int targetLevel,
        int footstoneLevel,
        (long Silver, long Gold)? cost,
        long guildSilver,
        long guildGold)
    {
        if (actorDuty < requiredDuty)
        {
            return GuildBuildingOutcome.NoAuthority;
        }

        if (cost is not { } price)
        {
            return GuildBuildingOutcome.TopRank;
        }

        if (buildingType != FootstoneType && targetLevel > footstoneLevel)
        {
            return GuildBuildingOutcome.FootstoneTooLow;
        }

        if (guildGold < price.Gold)
        {
            return GuildBuildingOutcome.ShortOfGold;
        }

        return guildSilver < price.Silver
            ? GuildBuildingOutcome.ShortOfSilver
            : GuildBuildingOutcome.Done;
    }

    public static GuildBuildingOutcome DecideDelete(
        int actorDuty,
        int requiredDuty,
        int buildingType)
    {
        if (buildingType == FootstoneType)
        {
            return GuildBuildingOutcome.FootstoneProtected;
        }

        return actorDuty < requiredDuty
            ? GuildBuildingOutcome.NoAuthority
            : GuildBuildingOutcome.Done;
    }
}
