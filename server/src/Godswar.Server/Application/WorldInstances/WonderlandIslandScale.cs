using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// WL-SLIDERS (the admin 2026-11-21, console d18 "keep, but also allow sliders for batch adjustments for islands"):
/// one HP % and one damage % slider per Wonderland island, 100 = the stock roster (WonderlandMonsterPlan).
/// HP applies to the island's hostile monsters when a run starts (allied island-5 helpers keep theirs);
/// damage applies at once to every hit a monster of that island lands on a player (normal hits, Fire Blast,
/// ground fire, the Atlas death blast and the Rock Spirit's reflection). A hit above 0 never drops below 1.
/// </summary>
internal static class WonderlandIslandScale
{
    public const string Category = "Wonderland islands";
    public const decimal MinimumPercent = 10m;
    public const decimal MaximumPercent = 500m;

    public static string HpKey(int island) => $"instance.wonderland_island{island}_hp_percent";
    public static string DamageKey(int island) => $"instance.wonderland_island{island}_damage_percent";

    private const string Source = "Application/WorldInstances/WonderlandIslandScale.cs";

    // Literal texts: editor/data/rate_text.json must carry the same words (tests/test_help_text_unit.py).
    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        new(HpKey(1), Category, "Island 1 monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 10, Source,
            "Health of every enemy on Wonderland island 1 (Alpha Demon and the monsters around it), in percent of normal. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(1), Category, "Island 1 monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 11, Source,
            "Damage every monster on Wonderland island 1 does to players, normal hits and special attacks, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
        new(HpKey(2), Category, "Island 2 monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 20, Source,
            "Health of every enemy on Wonderland island 2 (Monkeyface and Derskey and the monsters around it), in percent of normal. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(2), Category, "Island 2 monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 21, Source,
            "Damage every monster on Wonderland island 2 does to players, normal hits and special attacks, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
        new(HpKey(3), Category, "Island 3 monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 30, Source,
            "Health of every enemy on Wonderland island 3 (Flame Rooster and the monsters around it), in percent of normal. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(3), Category, "Island 3 monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 31, Source,
            "Damage every monster on Wonderland island 3 does to players, normal hits and special attacks, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
        new(HpKey(4), Category, "Island 4 monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 40, Source,
            "Health of every enemy on Wonderland island 4 (Rock Spirit and the monsters around it), in percent of normal. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(4), Category, "Island 4 monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 41, Source,
            "Damage every monster on Wonderland island 4 does to players, normal hits and special attacks, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
        new(HpKey(5), Category, "Island 5 monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 50, Source,
            "Health of every enemy on Wonderland island 5 (the enemy Marshal and the monsters around it), in percent of normal. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(5), Category, "Island 5 monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 51, Source,
            "Damage every monster on Wonderland island 5 does to players, normal hits and special attacks, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
        new(HpKey(6), Category, "Island 6 monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 60, Source,
            "Health of every enemy on Wonderland island 6 (Platinum Dragon and the monsters around it), in percent of normal. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(6), Category, "Island 6 monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 61, Source,
            "Damage every monster on Wonderland island 6 does to players, normal hits and special attacks, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
        new(HpKey(7), Category, "Island 7 monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 70, Source,
            "Health of every enemy on Wonderland island 7 (Multi-headed and the monsters around it), in percent of normal. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(7), Category, "Island 7 monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 71, Source,
            "Damage every monster on Wonderland island 7 does to players, normal hits and special attacks, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
        new(HpKey(8), Category, "Island 8 monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 80, Source,
            "Health of every enemy on Wonderland island 8 (the four final bosses and the Chest Guard and the monsters around it), in percent of normal. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(8), Category, "Island 8 monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 81, Source,
            "Damage every monster on Wonderland island 8 does to players, normal hits and special attacks, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
    ];

    public static decimal HpPercent(int island) => Percent(HpKey(island), island);
    public static decimal DamagePercent(int island) => Percent(DamageKey(island), island);

    private static decimal Percent(string key, int island) => island is < 1 or > 8
        ? 100m
        : Math.Clamp(ServerRates.Current.Get(key), MinimumPercent, MaximumPercent);

    public static uint ScaleHp(int island, uint health)
    {
        var percent = HpPercent(island);
        if (percent == 100m || health == 0) return health;
        return (uint)Math.Clamp(decimal.Round(health * percent / 100m, 0, MidpointRounding.AwayFromZero), 1m, uint.MaxValue);
    }

    public static uint ScaleDamage(int island, uint damage)
    {
        var percent = DamagePercent(island);
        if (percent == 100m || damage == 0) return damage;
        return (uint)Math.Clamp(decimal.Round(damage * percent / 100m, 0, MidpointRounding.AwayFromZero), 1m, uint.MaxValue);
    }
}
