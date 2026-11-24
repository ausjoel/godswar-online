using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Limits;

/// <summary>
/// W16 (the admin d129: "build end game switch - this may be utilised by others who host this project"): ONE hot rate,
/// <c>endgame.enabled</c>, that opens or closes the whole end-game path together. Before W16 a host had to flip two
/// rates and keep them in step: <c>limits.original_gw</c> (Class Suit IV-VII, quality 20, +25, 4 sockets, Adamantium)
/// and <c>gem.god_gem_drops</c> (the Olympian gems, the only material for +25 and weapon rank 14).
/// <list type="bullet">
/// <item>0 = off: the Original GodsWar limits and no god gems, whatever the two rates hold.</item>
/// <item>1 = on: the Origin limits and god gems from the world bosses, whatever the two rates hold.</item>
/// <item>2 = separate switches (default): the two rates decide, exactly as before W16.</item>
/// </list>
/// The default 2 changes nothing on any existing database (the start-up upsert inserts the row at its default), so an
/// upgrade never opens or closes anything by itself. Valour Laurels, Aether Crests and the Sanctum's Radiant and Primal
/// gems drop in every mode (d88); the god gem chance stays <c>gem.god_gem_drop_chance_bp</c>.
/// </summary>
internal static class EndgameSwitch
{
    public const string RateKey = "endgame.enabled";

    public const decimal Off = 0m;
    public const decimal On = 1m;
    public const decimal Separate = 2m;

    public static ServerRateDefinition Definition { get; } = new(
        RateKey, "Game version limits", "End-game master switch (0 off, 1 on, 2 separate switches)", "0/1/2",
        ServerRateKind.Absolute, Separate, Off, Separate, 1m, 0, "Application/Limits/EndgameSwitch.cs",
        "Opens or closes the whole end-game path in one place. " +
        "0 = off: Original GodsWar limits (Class Suit up to III, +12) and no god gems. " +
        "1 = on: Origin limits (Class Suit IV to VII, +25 with the rare gems, weapon ranks 11 to 14) and god gems " +
        "from the world bosses. " +
        "2 = the Original GodsWar limits switch and the god gem drop switch decide (the default).");

    /// <summary>The master's mode; an out-of-range stored value counts as 2 (the snapshot clamps it anyway).</summary>
    public static decimal Mode(ServerRatesSnapshot rates) =>
        decimal.Truncate(rates.Get(RateKey)) switch
        {
            Off => Off,
            On => On,
            _ => Separate
        };

    /// <summary>True when gear stops at the Original GodsWar limits (Class Suit III, Q10, +12).</summary>
    public static bool OriginalLimits(ServerRatesSnapshot rates) => Mode(rates) switch
    {
        Off => true,
        On => false,
        _ => rates.Absolute(GameLimitProfile.RateKey) == 1
    };

    /// <summary>True when the 19 world bosses may roll an Olympian god gem.</summary>
    public static bool GodGemDrops(ServerRatesSnapshot rates) => Mode(rates) switch
    {
        Off => false,
        On => true,
        _ => rates.Get(Finalise2RateKeys.GodGemDrops) >= 1m
    };

    /// <summary>One log line naming the mode and what it gives, for start-up and every reload that moves it.</summary>
    public static string Describe(ServerRatesSnapshot rates)
    {
        var mode = Mode(rates) switch { Off => "off", On => "on", _ => "separate" };
        var open = !OriginalLimits(rates);
        return $"[endgame] master={mode} limits={(open ? "origin" : "original")} " +
               $"class_suit_max={(open ? "VII" : "III")} god_gems={(GodGemDrops(rates) ? "on" : "off")} " +
               $"path={(open && GodGemDrops(rates) ? "OPEN" : open ? "OPEN-WITHOUT-G25" : "CLOSED")}";
    }
}
