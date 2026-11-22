using Godswar.Server.Application.Rates;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Application.Combat;

/// <summary>
/// B22 B23-F (the admin d86/d87, design: Development/batch-2026-11-22/P1/PVE-REPORT.md section 4): a burning ground field
/// for the Warrior, Champion and Priest, built like the Mage's Flame Blast (ground target, 10 passes 1 s apart, every
/// cast its own field). GENERATED names and numbers (P1): Warrior "Earthsplitter" 100-104 (physical), Champion
/// "Storm of Spears" 360-364 (physical), Priest "Holy Pyre" 870-874 (magic). Ranks 1-5, learnt from books at Lv10 / 34 /
/// 58 / 82 / 106. The Mage's Flame Blast is unchanged (it still never hits players).
/// <para>
/// Unlike Flame Blast these fields also hit ENEMY PLAYERS (the same admission as a normal hit: other faction, PvP map,
/// outside safe zones, duels; switch pvp.skills) at combat.field_pvp_percent of the damage (default 50; 0 = monsters
/// only, 100 = full). The area-balance sliders (combat.aoe_*) only change damage on monsters, as everywhere else.
/// </para>
/// </summary>
internal static class ClassFieldSkills
{
    public const string PvpPercentKey = "combat.field_pvp_percent";
    public const decimal DefaultPvpPercent = 50m;

    // family first id -> (profession, name). Ranks are first..first+4.
    private static readonly (int First, byte Profession, string Name)[] Families =
    [
        (100, 0, "Earthsplitter"),
        (360, 1, "Storm of Spears"),
        (870, 2, "Holy Pyre"),
    ];

    public static IReadOnlyList<int> SkillIds { get; } =
        Families.SelectMany(static family => Enumerable.Range(family.First, 5)).ToArray();

    public static bool IsField(int skillId) =>
        TryGetFamily(skillId, out _, out _);

    public static bool TryGetFamily(int skillId, out byte profession, out string name)
    {
        foreach (var family in Families)
        {
            if (skillId >= family.First && skillId < family.First + 5)
            {
                profession = family.Profession;
                name = family.Name;
                return true;
            }
        }

        profession = 0;
        name = string.Empty;
        return false;
    }

    /// <summary>The hot share of the damage a field deals to an enemy player, 0-100 (read on every pass).</summary>
    public static decimal PvpPercent(ServerRatesSnapshot rates) =>
        Math.Clamp(rates.Get(PvpPercentKey), 0m, 100m);

    public static decimal PvpPercent() => PvpPercent(ServerRates.Current);

    /// <summary>The resolution with its damage scaled to <paramref name="percent"/>; a landed hit keeps at least 1.</summary>
    public static CombatResolution ScaleForPlayers(decimal percent, in CombatResolution resolution) =>
        percent >= 100m ? resolution : AreaSkillBalance.Apply(percent / 100m, resolution);

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        new(PvpPercentKey, AreaSkillBalance.Category, "Class fields: damage to enemy players", "%",
            ServerRateKind.Absolute, DefaultPvpPercent, 0m, 100m, 5m, 5,
            "Application/Combat/ClassFieldSkills.cs",
            "How much of their damage the Warrior, Champion and Priest burning fields (Earthsplitter, Storm of " +
            "Spears, Holy Pyre) deal to enemy players standing in them (PvP maps, other faction, duels). 50 = half " +
            "damage, 100 = full damage, 0 = they only hurt monsters. Monsters always take full damage. The Mage's " +
            "Flame Blast never hits players."),
        new(Godswar.Server.Application.World.Content.ClassFieldBookLootRule.SwitchKey,
            Godswar.Server.Application.Finalise2.Finalise2Rates.Category,
            "Class field books: drop with Flame Blast books", "0/1", ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 75,
            "Application/World/Content/ClassFieldBookLootRule.cs",
            "On: whenever a monster drops a Flame Blast book, a Warrior, Champion or Priest who killed it also gets " +
            "their own class field book of the same rank (Earthsplitter, Storm of Spears, Holy Pyre). Same chance as " +
            "the Mage's book; the Flame Blast book still drops. Off: the class field books do not drop."),
    ];
}
