using Godswar.Server.Application.Rates;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Application.Combat;

/// <summary>
/// B21 (item 18; the admin W5 request: "Mages have the unfair advantage of their flame blast doing frequent casts and aoe -
/// they crush in instances"). One hot multiplier per class for the damage of AREA attack skills on monsters (self-centred
/// and ground-targeted, Flame Blast's recurring field included). Default 1 for every class = today's damage; single-target
/// skills, basic attacks and PvP are never touched. Applied right after the damage formula on each of the three PvE area
/// paths (ECS cast: PlayerCombatIntentSystem.SkillResolution; legacy cast: GameClientHandler.CombatArea; Flame Blast field
/// pulses: GameClientHandler.FlameBlastPulse), before fixed-damage rules such as capturable pets' one damage per hit.
/// </summary>
internal static class AreaSkillBalance
{
    public const string WarriorKey = "combat.aoe_warrior";
    public const string ChampionKey = "combat.aoe_champion";
    public const string PriestKey = "combat.aoe_priest";
    public const string MageKey = "combat.aoe_mage";
    public const string Category = "Combat: area skill balance";

    // Profession codes as stored on the character (TrainingDummyHostileStatusSkillCatalog).
    public static string? KeyFor(byte profession) => profession switch
    {
        0 => WarriorKey,
        1 => ChampionKey,
        2 => PriestKey,
        3 => MageKey,
        _ => null
    };

    public static decimal RateFor(byte profession) =>
        KeyFor(profession) is { } key ? ServerRates.Current.Get(key) : 1m;

    /// <summary>The resolution with its damage scaled; a landed hit never drops below 1. Rate 1 returns it unchanged.</summary>
    public static CombatResolution Apply(byte profession, in CombatResolution resolution) =>
        Apply(RateFor(profession), resolution);

    public static CombatResolution Apply(decimal rate, in CombatResolution resolution)
    {
        if (rate == 1m || !resolution.Hit || resolution.Damage == 0) return resolution;
        var scaled = decimal.Round(resolution.Damage * rate, 0, MidpointRounding.AwayFromZero);
        var damage = (uint)Math.Clamp(scaled, 1m, uint.MaxValue);
        return resolution with { Damage = damage };
    }

    private static ServerRateDefinition Rate(string key, string label, int order, string text) =>
        new(key, Category, label, "x", ServerRateKind.Multiplier, 1m, 0.1m, 5m, 0.05m, order,
            "Application/Combat/AreaSkillBalance.cs", text);

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        Rate(WarriorKey, "Warrior: area skill damage", 1,
            "Multiplies the damage Warrior area skills (Sword Storm, Steel Rain) deal to monsters. " +
            "x1 = normal damage. Single-target skills, normal attacks and damage to players do not " +
            "change."),
        Rate(ChampionKey, "Champion: area skill damage", 2,
            "Multiplies the damage Champion area skills (Ruined Strike, Chip Blast, Spear Blast, " +
            "Meteor Blast) deal to monsters. x1 = normal damage. Single-target skills, normal " +
            "attacks and damage to players do not change."),
        Rate(PriestKey, "Priest: area skill damage", 3,
            "Multiplies the damage Priest area skills (Stalagmite, Ternary Curse) deal to " +
            "monsters. x1 = normal damage. Single-target skills, healing, normal attacks and " +
            "damage to players do not change."),
        Rate(MageKey, "Mage: area skill damage", 4,
            "Multiplies the damage Mage area skills (Flame Blast and every pass of its burning " +
            "ground, Star Shower, Thundercloud, Fire Blast) deal to monsters. x1 = normal damage. " +
            "For example x0.6 takes Flame Blast from about 22,000 to about 13,000 per cast on " +
            "each monster at level 100. Single-target skills, normal attacks and damage to " +
            "players do not change."),
    ];
}
