namespace Godswar.Server.Game;

/// <summary>
/// ARENA build (2026-11-03): the three switches of the arena task. Each one is
/// OFF unless its environment variable says <c>1</c>/<c>true</c>/<c>on</c>, so a
/// binary carrying this code behaves exactly like the build before it until an
/// operator turns a feature on (systemd drop-in, then a restart).
/// <list type="bullet">
/// <item><c>GODSWAR_DUEL_ARENA_PVP</c>: PvP between any two players in the Duel
/// Arena combat area (IGG "Duel Arena", igg-acid=372).</item>
/// <item><c>GODSWAR_PARNASSUS</c>: the Battlefield Transporter's Parnassus button
/// (IGG "Parnassus", igg-acid=371) and the arrival/revive points on map 68.</item>
/// <item><c>GODSWAR_AFK_REVIVAL</c>: the opt-in automatic revival that costs Gold
/// (IGG "AFK System", igg-acid=155). <c>GODSWAR_AFK_REVIVAL_GOLD</c> overrides the
/// guide's 10 Gold.</item>
/// </list>
/// Protocol checks set <see cref="Override"/> to test both states in one process.
/// </summary>
internal static class ArenaFeatureGates
{
    internal const string DuelArenaVariable = "GODSWAR_DUEL_ARENA_PVP";
    internal const string ParnassusVariable = "GODSWAR_PARNASSUS";
    internal const string AfkRevivalVariable = "GODSWAR_AFK_REVIVAL";
    internal const string AfkRevivalGoldVariable = "GODSWAR_AFK_REVIVAL_GOLD";

    /// <summary>IGG "AFK System": "each revival costs 10 Gold" (the GodsArena wiki says 15).</summary>
    internal const int GuideAfkRevivalGold = 10;

    /// <summary>Test-only: forces every gate on (true) or off (false); null reads the environment.</summary>
    internal static bool? Override { get; set; }

    public static bool DuelArenaPvp => Read(DuelArenaVariable);

    public static bool Parnassus => Read(ParnassusVariable);

    public static bool AfkRevival => Read(AfkRevivalVariable);

    public static int AfkRevivalGold =>
        int.TryParse(
            Environment.GetEnvironmentVariable(AfkRevivalGoldVariable),
            out var value) && value is >= 0 and <= 1_000_000
            ? value
            : GuideAfkRevivalGold;

    private static bool Read(string variable)
    {
        if (Override is { } forced)
        {
            return forced;
        }

        var value = Environment.GetEnvironmentVariable(variable)?.Trim();
        return value is not null &&
            (value == "1" ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
