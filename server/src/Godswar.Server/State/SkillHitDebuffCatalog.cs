using System.Collections.Frozen;

namespace Godswar.Server.State;

internal enum SkillHitDebuffKind : byte
{
    Weakness = 1,
    Blinded = 2
}

/// <summary>Weakness: damage taken +Amount basis points. Blinded: the target's Hit Rating -Amount.</summary>
internal readonly record struct SkillHitDebuffDefinition(
    int SkillId,
    uint StatusId,
    SkillHitDebuffKind Kind,
    int Amount,
    int DurationMilliseconds);

/// <summary>
/// W2 skill statuses: Weakness = Ice Cone 1-5 (820-824; Status.ini 140-143, effects 11/13 = -5..-10 % damage
/// reduction, read as +5..+10 % damage taken so it also works on monsters with no reduction, 8-10 s).
/// Blinded = Holy Light 4/5 (843/844; status 1432, Hit Rating -200 for 5 s).
/// Ice Cone 4/5's second status (Frozen 1433/1436) is in SkillHitStunCatalog.
/// </summary>
internal static class SkillHitDebuffCatalog
{
    private static readonly FrozenDictionary<int, SkillHitDebuffDefinition> Definitions =
        new SkillHitDebuffDefinition[]
        {
            new(820, 140, SkillHitDebuffKind.Weakness, 500, 8_000),
            new(821, 141, SkillHitDebuffKind.Weakness, 600, 9_000),
            new(822, 142, SkillHitDebuffKind.Weakness, 800, 10_000),
            new(823, 143, SkillHitDebuffKind.Weakness, 1_000, 10_000),
            new(824, 143, SkillHitDebuffKind.Weakness, 1_000, 10_000),
            new(843, 1432, SkillHitDebuffKind.Blinded, 200, 5_000),
            new(844, 1432, SkillHitDebuffKind.Blinded, 200, 5_000)
        }.ToFrozenDictionary(static definition => definition.SkillId);

    public static int Count => Definitions.Count;

    public static bool TryGet(int skillId, out SkillHitDebuffDefinition definition) =>
        Definitions.TryGetValue(skillId, out definition);
}
