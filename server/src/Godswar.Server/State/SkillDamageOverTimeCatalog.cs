using System.Collections.Frozen;

namespace Godswar.Server.State;

internal enum SkillDamageOverTimeFamily : byte
{
    Bleeding = 1,
    Electrocuted = 2,
    Burned = 3
}

/// <summary>
/// One player skill's periodic-damage status, copied from the client's Magic.ini
/// (Status=) and Status.ini (Effect 27: Values = damage per tick, Interval, Time).
/// </summary>
internal readonly record struct SkillDamageOverTimeDefinition(
    int SkillId,
    uint StatusId,
    SkillDamageOverTimeFamily Family,
    int Priority,
    int TickDamage,
    int IntervalMilliseconds,
    int TickCount)
{
    public int DurationMilliseconds => IntervalMilliseconds * TickCount;
}

/// <summary>
/// W2 skill statuses (systems map gap 5): the 16 player skills whose stock status is
/// damage over time and that the server did not apply. Bleeding = Blood Claw 50-54 and
/// Rage Claw 60-64 (2 s ticks for 6 s; status 14's Time=674 is a stock typo, read as 6).
/// Electrocuted = Thunder 533/534 and Thundercloud 563/564 (1 s for 10 s). Burned =
/// Fire Lotus 543/544 (1 s for 8 s). Monsters only: skills cannot target players yet.
/// </summary>
internal static class SkillDamageOverTimeCatalog
{
    private static readonly FrozenDictionary<int, SkillDamageOverTimeDefinition> Definitions =
        new SkillDamageOverTimeDefinition[]
        {
            Bleed(50, 10, 1, 50), Bleed(51, 11, 2, 100), Bleed(52, 12, 3, 200),
            Bleed(53, 13, 4, 350), Bleed(54, 13, 4, 350),
            Bleed(60, 14, 5, 120), Bleed(61, 15, 6, 250), Bleed(62, 16, 7, 500),
            Bleed(63, 17, 8, 800), Bleed(64, 17, 8, 800),
            new(533, 1440, SkillDamageOverTimeFamily.Electrocuted, 1, 500, 1_000, 10),
            new(534, 1441, SkillDamageOverTimeFamily.Electrocuted, 2, 600, 1_000, 10),
            new(563, 1440, SkillDamageOverTimeFamily.Electrocuted, 1, 500, 1_000, 10),
            new(564, 1441, SkillDamageOverTimeFamily.Electrocuted, 2, 600, 1_000, 10),
            new(543, 1442, SkillDamageOverTimeFamily.Burned, 1, 600, 1_000, 8),
            new(544, 1443, SkillDamageOverTimeFamily.Burned, 2, 800, 1_000, 8)
        }.ToFrozenDictionary(static definition => definition.SkillId);

    public static int Count => Definitions.Count;

    public static IEnumerable<SkillDamageOverTimeDefinition> All => Definitions.Values;

    public static bool TryGet(int skillId, out SkillDamageOverTimeDefinition definition) =>
        Definitions.TryGetValue(skillId, out definition);

    private static SkillDamageOverTimeDefinition Bleed(int skillId, uint statusId, int priority, int tickDamage) =>
        new(skillId, statusId, SkillDamageOverTimeFamily.Bleeding, priority, tickDamage, 2_000, 3);
}
