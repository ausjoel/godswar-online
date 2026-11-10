using System.Collections.Frozen;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// Sigil Stones. Damage is the item text; skill, target and range are Magic.ini (Distance 10, Range 10 on the area stones).
/// Monsters only. The number goes through the normal skill formula as flat magic power, so defence reduces it and it can miss.
/// </summary>
internal static class SigilStonePolicy
{
    private static readonly FrozenDictionary<uint, (int SkillId, int Damage, bool Area)> ByItemId =
        new Dictionary<uint, (int SkillId, int Damage, bool Area)>
        {
            [9120] = (4877, 1_800, false),
            [9121] = (4878, 2_920, false),
            [9122] = (4879, 4_320, false),
            [9123] = (4880, 6_170, false),
            [9124] = (4881, 8_470, false),
            [9125] = (4882, 11_250, false),
            [9126] = (4883, 18_140, false),
            [9140] = (4884, 1_260, true),
            [9141] = (4885, 3_020, true),
            [9142] = (4886, 4_310, true),
            [9143] = (4887, 7_870, true),
            [9144] = (4888, 14_510, true)
        }.ToFrozenDictionary();

    internal static IEnumerable<KeyValuePair<uint, ProgressItemGrant>> ProgressGrants =>
        ByItemId.Select(static pair => KeyValuePair.Create(pair.Key,
            new ProgressItemGrant(pair.Value.SkillId, ProgressItemKind.Sigil, pair.Value.Damage)));

    public static bool TryResolve(IItemTemplateCatalog templates, uint itemId, out SkillCombatDefinition combat)
    {
        combat = default;
        if (!ByItemId.TryGetValue(itemId, out var stone) ||
            !BagConsumableItemStats.TryResolveSkillId(templates, itemId, out var skill) || skill != stone.SkillId) return false;
        combat = new(stone.SkillId, stone.Area ? 16 : 44, 28, 10f, stone.Area ? 10f : 0f, 1, 0, -1m, stone.Damage);
        return true;
    }
}
