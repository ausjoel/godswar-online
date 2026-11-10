namespace Godswar.Server.State;

/// <summary>
/// W2 friendly skill statuses carried by the Priest heals (Magic.ini Status= on top of the instant heal).
/// <list type="bullet">
/// <item>Area Heal 4 (763) -> Status 1434 Radiance of Renewal: kind 1001, priority 4, Effect 26 AddHP 1500, Interval 1, Time 8.</item>
/// <item>Area Heal 5 (764) -> Status 1435 Grace of Vitality: kind 1001, priority 4, Effect 26 AddHP 1750, Interval 1, Time 8.</item>
/// <item>Heal 4/5 (753/754) -> Status 1439 Divine Intervention: kind 1002, priority 1, Effect 36 "Remove Debuff", Time 1.</item>
/// </list>
/// </summary>
/// <remarks>
/// The heal over time rides the item HoT tick (<see cref="SkillStatusEffectDefinition.HpPerSecond"/>), so it heals,
/// publishes and persists vitals exactly like items 4013-4016. Not applied: the Status.ini note's "multiplier bonus for
/// each nearby enemy and players" (no formula exists; the same choice as items 9100-9104). Magic.ini StatusOdds
/// (-100 / -50) is a native rating, not a percent, so the status always applies while its switch is on (GENERATED rule).
/// Ranks 1-3 of both heals carry no status (Magic.ini Status=-1).
/// </remarks>
internal static class FriendlySkillStatusCatalog
{
    public const uint DivineInterventionStatusId = 1439;
    public const int DivineInterventionKind = 1002;

    private static readonly SkillStatusEffectDefinition Radiance =
        new(763, 1434, 1001, 4, true, TimeSpan.FromSeconds(8), TimeSpan.Zero, 0, 0) { HpPerSecond = 1_500 };

    private static readonly SkillStatusEffectDefinition Grace =
        new(764, 1435, 1001, 4, true, TimeSpan.FromSeconds(8), TimeSpan.Zero, 0, 0) { HpPerSecond = 1_750 };

    public static bool TryGetHealOverTime(int skillId, out SkillStatusEffectDefinition definition)
    {
        definition = skillId switch
        {
            763 => Radiance,
            764 => Grace,
            _ => default
        };
        return definition.StatusId != 0;
    }

    /// <summary>The 1 s Divine Intervention icon shown after the cleanse (no stat effect).</summary>
    public static bool TryGetCleanse(int skillId, out SkillStatusEffectDefinition icon)
    {
        icon = skillId is 753 or 754
            ? new(skillId, DivineInterventionStatusId, DivineInterventionKind, 1, true,
                TimeSpan.FromSeconds(1), TimeSpan.Zero, 0, 0)
            : default;
        return icon.StatusId != 0;
    }

    /// <summary>Stock HP per second scaled by the percent rate (100 = stock).</summary>
    public static int ScaleHealPerSecond(int stock, int percent) =>
        (int)Math.Clamp((long)stock * Math.Max(0, percent) / 100, 0, int.MaxValue);
}
