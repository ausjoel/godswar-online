using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal static class MoneyBagPolicy
{
    // Use/Skill come from pinned item content; amounts from the reviewed native Magic.ini table.
    public static bool TryResolve(IItemTemplateCatalog templates, uint itemId, out BagConsumableEffect effect)
    {
        effect = default;
        return BagConsumableItemStats.TryResolveSkillId(templates, itemId, out var skill) &&
            BagConsumableEffectCatalog.TryResolve(skill, out effect) && effect.IsValid &&
            effect.Kind == BagConsumableEffectKind.GrantSilver;
    }
}
