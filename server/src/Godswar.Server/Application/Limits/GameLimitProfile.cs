using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Limits;

/// <summary>
/// W4F (wave 4 item 6): one hot switch between the original GodsWar limits and the Origin/Reborn limits this server
/// ships with. Read per operation, so a change applies on the next forge / drill / upgrade without a restart.
/// Only further progression is refused: stored items above the Original limits keep working and are never changed.
/// Refusals reuse the existing native result of each operation (no new messages or pop-ups).
/// </summary>
internal static class GameLimitProfile
{
    public const string RateKey = "limits.original_gw";

    /// <summary>Original GW: Mystic is Q10 (ItemColor.xml BaseLevel10).</summary>
    public const short OriginalMaximumQuality = 10;
    public const short OriginalMaximumGrade = 12;
    public const short OriginalMaximumSockets = 2;
    /// <summary>Platinum (Holy Suit type 4) level 10 is the top; Mithril (5) and above are refused.</summary>
    public const short OriginalMaximumHolySuitType = 4;

    // B22 B25-G: the description names what Off opens (Class Suit V-VII, rare gems, ranks 11-14).
    public static ServerRateDefinition Definition { get; } = new(
        RateKey, "Game version limits", "Original GodsWar limits (instead of Origin/Reborn; used while the end-game master is 2)", "0/1",
        ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 1, "Application/Limits/GameLimitProfile.cs",
        "Chooses how far gear can progress. On: Original GodsWar limits: forging stops at Mystic " +
        "quality and +12, 2 holy stone sockets, Holy Suit up to Platinum 10, Class Suit up to " +
        "III. Off: Origin limits: quality 20, +25, 4 sockets, Adamantium 10, Class Suit IV to VII " +
        "(Titan, Primordial, Aether; with the rare gems for +21 to +25 and weapon ranks 11 to 14). Items " +
        "already above a limit keep working and are never changed.");

    // W16: the end-game master (endgame.enabled) decides first; at 2 (default) this rate decides as before.
    public static bool Original => EndgameSwitch.OriginalLimits(ServerRates.Current);

    /// <summary>Quality a Sapphire forge may not go past (the item must be below it to forge).</summary>
    public static short MaximumQuality(short reborn) => Original ? Math.Min(reborn, OriginalMaximumQuality) : reborn;

    public static short MaximumGrade(short reborn) => Original ? Math.Min(reborn, OriginalMaximumGrade) : reborn;

    public static short MaximumSockets(short reborn) => Original ? Math.Min(reborn, OriginalMaximumSockets) : reborn;

    public static bool HolySuitTargetAllowed(short targetSuitType) =>
        !Original || targetSuitType <= OriginalMaximumHolySuitType;

    public static bool ClassSuitTierIVAllowed => !Original;
}
