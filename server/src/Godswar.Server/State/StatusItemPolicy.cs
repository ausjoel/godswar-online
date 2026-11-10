using System.Collections.Frozen;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// Bag items the client lists under StatusMagic whose status is a heal over time or a combat bonus.
/// Skill, status, kind, priority, time and amounts are Magic.ini Status= and Status.ini Values/Time.
/// </summary>
/// <remarks>
/// Effect numbers are the Status.ini legend: 12/14 damage dealt, 18 hit, 20/25 defence, 21/24 attack, 26 HP, 28 MP.
/// Not applied: Effect 17 (max HP) of 9160-9164, the "bonus per nearby player" of 9100-9104 and their AffectObj=3
/// range (no formula exists, so they heal the user for the flat value). Poisoned Cake 3986 is absent: its target is unknown.
/// Olive Branch: 4541-4543 are Status 650-652 (Kind 80-82, Effect ForPeace, 30 minutes). 4120 has Status -1 in Magic.ini; it borrows 650 for the hour its text states.
/// </remarks>
internal static class StatusItemPolicy
{
    private static SkillStatusEffectDefinition Heal(int skill, uint status, int kind, int priority, int seconds,
        int hp = 0, int mp = 0) =>
        new(skill, status, kind, priority, true, TimeSpan.FromSeconds(seconds), TimeSpan.Zero, 0, 0)
        { HpPerSecond = hp, MpPerSecond = mp };

    private static SkillStatusEffectDefinition Attack(int skill, uint status, int kind, int priority, int seconds,
        int physical, int magic, int hit = 0) =>
        new(skill, status, kind, priority, true, TimeSpan.FromSeconds(seconds), TimeSpan.Zero, hit, 0)
        { PhysicalAttackBonus = physical, MagicAttackBonus = magic };

    private static SkillStatusEffectDefinition Damage(int skill, uint status, int kind, int seconds, int basisPoints) =>
        new(skill, status, kind, 1, true, TimeSpan.FromSeconds(seconds), TimeSpan.Zero, 0, 0)
        { PhysicalDamageBonusBasisPoints = basisPoints, MagicDamageBonusBasisPoints = basisPoints };

    private static SkillStatusEffectDefinition Guard(int skill, uint status, int priority, int seconds,
        int physical, int magic) =>
        new(skill, status, 56, priority, true, TimeSpan.FromSeconds(seconds), TimeSpan.Zero, 0, 0,
            PhysicalDefenseBonus: physical, MagicDefenseBonus: magic);

    private static SkillStatusEffectDefinition Peace(int skill, uint status, int kind, int seconds) =>
        new(skill, status, kind, 1, true, TimeSpan.FromSeconds(seconds), TimeSpan.Zero, 0, 0);

    internal static bool IsPeace(uint itemId) => itemId is 4120 or 4541 or 4542 or 4543;

    // Text of 4542: not Argolis or Derveni. 4543: not Megara Coast or Plataea. All: no dungeons or the Pindus battlefield.
    internal static bool PeaceAllowedOn(uint itemId, byte mapId, bool dungeon) => !dungeon && mapId != 38 && itemId switch
    {
        4542 => mapId is not (16 or 15),
        4543 => mapId is not (18 or 19),
        _ => true
    };

    private static readonly FrozenDictionary<uint, SkillStatusEffectDefinition> ByItemId =
        new Dictionary<uint, SkillStatusEffectDefinition>
        {
            [4120] = Peace(4511, 650, 80, 3_600),
            [4541] = Peace(5532, 650, 80, 1_800),
            [4542] = Peace(5533, 651, 81, 1_800),
            [4543] = Peace(5534, 652, 82, 1_800),
            [4013] = Heal(4852, 600, 53, 1, 5, hp: 1_000),
            [4014] = Heal(4853, 601, 53, 2, 6, hp: 1_500),
            [4015] = Heal(4854, 602, 53, 3, 8, hp: 2_500),
            [4016] = Heal(4855, 603, 53, 4, 10, hp: 3_600),
            [4510] = Heal(4810, 520, 15, 1, 30, hp: 500),
            [4511] = Heal(4811, 521, 17, 1, 3_600, hp: 100),
            [4512] = Heal(3127, 510, 19, 1, 3_600, mp: 5),
            [4513] = Heal(5137, 557, 30, 1, 30, hp: 500),
            [9100] = Heal(4856, 604, 54, 1, 6, hp: 1_050),
            [9101] = Heal(4857, 605, 54, 2, 7, hp: 1_260),
            [9102] = Heal(4858, 606, 54, 3, 7, hp: 1_470),
            [9103] = Heal(4859, 607, 54, 4, 8, hp: 1_750),
            [9104] = Heal(4860, 608, 54, 5, 10, hp: 2_520),
            [4505] = Damage(4813, 571, 21, 3_600, 1_000),
            [4520] = Damage(4820, 540, 16, 60, 3_000),
            [4508] = Attack(4850, 554, 30, 1, 1_800, 100, 80),
            [4509] = Attack(4851, 555, 30, 2, 30, 500, 400),
            [4514] = Attack(5138, 556, 30, 1, 30, 200, 200),
            [9180] = Attack(4872, 614, 55, 1, 1_800, 100, 80, hit: 8),
            [9181] = Attack(4873, 615, 55, 2, 300, 200, 160, hit: 20),
            [9182] = Attack(4874, 616, 55, 3, 300, 300, 240, hit: 32),
            [9183] = Attack(4875, 617, 55, 4, 30, 400, 320, hit: 68),
            [9184] = Attack(4876, 618, 55, 5, 30, 500, 400, hit: 160),
            [9160] = Guard(4861, 609, 1, 1_800, 50, 40),
            [9161] = Guard(4862, 610, 2, 300, 100, 80),
            [9162] = Guard(4863, 611, 3, 300, 200, 160),
            [9163] = Guard(4864, 612, 4, 30, 300, 240),
            [9164] = Guard(4865, 613, 5, 30, 400, 320)
        }.ToFrozenDictionary();

    internal static IEnumerable<KeyValuePair<uint, ProgressItemGrant>> ProgressGrants =>
        ByItemId.Select(static pair => KeyValuePair.Create(pair.Key,
            new ProgressItemGrant(pair.Value.SkillId, ProgressItemKind.StatusBuff, 1)));

    /// <summary>
    /// The item buffs the editor may apply without an item: one row per status id, peace left out
    /// (<see cref="PeaceAllowedOn"/> limits it by map, and only an item use checks the map).
    /// </summary>
    internal static IEnumerable<SkillStatusEffectDefinition> OperatorDefinitions =>
        ByItemId.Where(static pair => !IsPeace(pair.Key))
            .Select(static pair => pair.Value)
            .GroupBy(static definition => definition.StatusId)
            .Select(static group => group.First());

    public static bool TryResolve(IItemTemplateCatalog templates, uint itemId, out SkillStatusEffectDefinition definition) =>
        ByItemId.TryGetValue(itemId, out definition) &&
        BagConsumableItemStats.TryResolveSkillId(templates, itemId, out var skill) && skill == definition.SkillId;
}
