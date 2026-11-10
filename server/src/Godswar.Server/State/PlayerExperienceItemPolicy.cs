using Godswar.Server.Application.Items;
using Godswar.Server.Application.Rates; // ROUND2

namespace Godswar.Server.State;

internal static class PlayerExperienceItemPolicy
{
    internal const uint ItemId = 4174;
    internal const int ExperiencePerPill = 1_000_000;

    // ROUND2: one pill's EXP times exp.pill (rate 1 = 1,000,000).
    internal static int RatedExperiencePerPill =>
        ServerRates.Current.Multiply(ServerRateKeys.ExperiencePill, ExperiencePerPill, 100_000_000);

    // Millet Liqueur: +1 level (Chinese text, item key, skill name and 2008 script agree; the English "title" is a mistranslation).
    internal const uint LevelItemId = 4104;

    // W3 seasons mid-autumn: Bean / Red Mooncake, "increase EXP by 500 / 2000 points" (MoonCake11/12 texts; their
    // skills 4805/4806 are AddEXP5/6 with Power2 = 0, so the amount is the text's). Keyed by item: pet book 10551
    // also carries skill 4805. Paid only while season.mid_autumn is 1.
    internal static int MooncakeExperience(uint itemId) => itemId switch { 3810 => 500, 3811 => 2_000, _ => 0 };

    internal static bool IsExperienceItem(uint itemId) =>
        itemId == ItemId || itemId == LevelItemId || itemId is >= 4170 and <= 4173 || MooncakeExperience(itemId) > 0;

    // The EXP Pill keeps its rated amount; EXP Stones 4170-4173 pay their native Magic.ini Power2.
    internal static bool TryResolveAmount(IItemTemplateCatalog templates, uint itemId, out int amount)
    {
        amount = 0;
        if (!IsExperienceItem(itemId) || !templates.TryGet(itemId, out _)) return false;
        if (itemId == LevelItemId)
        {
            amount = 1; // The executor replaces this with what the character needs for the next level.
            return BagConsumableItemStats.TryResolveSkillId(templates, itemId, out _);
        }
        if (MooncakeExperience(itemId) is > 0 and var cake)
        {
            amount = cake;
            return Godswar.Server.Application.Seasons.MidAutumnSeason.SwitchOn &&
                BagConsumableItemStats.TryResolveSkillId(templates, itemId, out _);
        }
        if (itemId == ItemId)
        {
            amount = RatedExperiencePerPill;
            return BagConsumableItemStats.TryResolveSkillId(templates, itemId, out _);
        }
        if (!BagConsumableItemStats.TryResolveSkillId(templates, itemId, out var skill) ||
            !BagConsumableEffectCatalog.TryResolve(skill, out var effect) ||
            effect.Kind != BagConsumableEffectKind.GrantExperience || !effect.IsValid) return false;
        amount = effect.Amount;
        return true;
    }

    internal static bool TryApply(int level, long experience, bool sealedLevel,
        out PlayerExperienceProgression progression) =>
        TryApply(level, experience, sealedLevel, RatedExperiencePerPill, out progression);

    // ROUND2: the amount is explicit so stored evidence re-validates with the
    // amount it was granted with, whatever the rate is now.
    internal static bool TryApply(int level, long experience, bool sealedLevel,
        int amount, out PlayerExperienceProgression progression)
    {
        progression = default!;
        if (amount <= 0 ||
            level is < 1 or > PlayerExperienceCatalog.MaximumLevel ||
            experience < 0 || experience > PlayerExperienceCatalog.MaximumStoredExperience ||
            (!sealedLevel && level == PlayerExperienceCatalog.MaximumLevel) ||
            (sealedLevel && experience > PlayerExperienceCatalog.MaximumStoredExperience - amount))
            return false;

        progression = PlayerExperienceCatalog.Apply(level, experience, amount, sealedLevel);
        return progression.ExperienceGained == amount;
    }
}
