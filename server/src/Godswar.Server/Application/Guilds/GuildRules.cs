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
            "Makes guild buildings follow the original game's rules. On: each level costs the " +
            "guild Gold and Silver, no building may pass the Guild Footstone's level, and only " +
            "the ranks set below may build or demolish. Off: building is free and open to every " +
            "member.", defaultValue: 1m), // B21 item 17: default on (live is on)
        Duty(
            BuildDuty, 20, "Guild rank that may build/upgrade", 5m,
            "Infrastructure/Guilds/PostgresGuildStore.BuildingRules.cs",
            "Lowest guild rank that may build or upgrade a guild building. 6 Lord, 5 Deputy, 4 " +
            "Director, 3 Elite, 2 Insider, 1 Member. Used while 'Guild building rules' is on."),
        Duty(
            DeleteDuty, 30, "Guild rank that may demolish", 6m,
            "Infrastructure/Guilds/PostgresGuildStore.BuildingRules.cs",
            "Lowest guild rank that may demolish a guild building. 6 Lord, 5 Deputy, 4 Director, " +
            "3 Elite, 2 Insider, 1 Member. The Footstone can never be demolished."),
        Switch(
            AltarCurve, 40, "Altar offering bonus from the first point",
            "Application/Guilds/GuildAltarWorshipBonus.cs",
            "Chooses how the altar offering bonus grows. On: every 1,000 points held on an altar " +
            "add 1%, up to 150% at 150,000 points, as in the original game. Off: nothing below " +
            "50,000 points, then 7.5% per 50,000 up to 150% at 1,000,000.", defaultValue: 1m), // B21 item 17
        Switch(
            LordRules, 50, "Guild Lord hand-over",
            "Game/GameClientHandler.GuildMembers.cs, GuildExit.cs",
            "Lets a guild Lord hand over the guild. On: the Change Lord button works, and the " +
            "Lord cannot leave the guild while holding the title. Off: no hand-over, and the Lord " +
            "may leave.", defaultValue: 1m), // B21 item 17
        Switch(
            DoubleClaims, 60, "Member Advisor double EXP/TP claims",
            "Game/GameClientHandler.GuildWelfare.cs",
            "Lets guild members claim double EXP and double Talent hours from the Member Advisor. " +
            "On: claims come out of a weekly allowance that resets on Saturday. Off: the advisor " +
            "always answers 'insufficient time'.", defaultValue: 1m), // B21 item 17
        new ServerRateDefinition(
            DoubleLevel, Category,
            "Guild double EXP/TP level (hours per week = level + 2)",
            "level", ServerRateKind.Absolute, 2m, 0m, 22m, 1m, 70,
            "Game/GameClientHandler.GuildWelfare.cs",
            "The double EXP and double Talent level every guild has. Hours per week = this level " +
            "+ 2, so 2 = 4 hours a week of each. The game's highest level is 22."),
        Switch(
            QuestRules, 80, "Guild quests",
            "Game/GameClientHandler.GuildWelfare.cs, GameClientHandler.Quests.cs",
            "Makes guild quests work as guild quests. On: an officer must release them each day, " +
            "the player needs a guild, and the rewards go to the guild (Guild Gold, Guild Fund) " +
            "and the member (Contribution). Off: anyone may take them and the 'Guild Gold' is " +
            "paid to the player. Turn it on only once guilds exist, or players lose these daily " +
            "quests.",
            defaultValue: 1m), // B21 item 17: default on (live is on)
        Duty(
            QuestReleaseDuty, 90, "Guild rank that may release guild quests", 4m,
            "Game/GameClientHandler.GuildWelfare.cs",
            "Lowest guild rank that may release the day's guild quests. 6 Lord, 5 Deputy, 4 " +
            "Director, 3 Elite, 2 Insider, 1 Member. Used while 'Guild quests' is on.")
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
