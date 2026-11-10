using System.Collections.Frozen;

namespace Godswar.Server.State;

/// <summary>
/// W2 skill statuses: area skills whose stock status stops the target (Status.ini Effect 2-6 = no moving,
/// casting, attacking or items). Dizzy = Steel Rain 4/5 (43/44, statuses 1446/1447: 0.5 s / 1 s).
/// Frozen = Ice Cone 4/5 (823/824, statuses 1433/1436: 0.5 s / 1 s, beside their Weakness).
/// Concussed = Fire Blast 4/5 (583/584, status 1444: 2 s; its 0.75 damage-taken values are not applied).
/// Applied to surviving non-boss monsters through the elemental Shock stun plan.
/// </summary>
internal static class SkillHitStunCatalog
{
    private static readonly FrozenDictionary<int, (uint StatusId, int DurationMilliseconds)> Definitions =
        new Dictionary<int, (uint, int)>
        {
            [43] = (1446, 500),
            [44] = (1447, 1_000),
            [583] = (1444, 2_000),
            [584] = (1444, 2_000),
            [823] = (1433, 500),
            [824] = (1436, 1_000)
        }.ToFrozenDictionary();

    public static int Count => Definitions.Count;

    public static bool TryGet(int skillId, out uint statusId, out int durationMilliseconds)
    {
        if (Definitions.TryGetValue(skillId, out var value))
        {
            (statusId, durationMilliseconds) = value;
            return true;
        }

        statusId = 0;
        durationMilliseconds = 0;
        return false;
    }
}
