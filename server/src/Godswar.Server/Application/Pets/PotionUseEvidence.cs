using Godswar.Server.Application.Characters;
using Godswar.Server.State;

namespace Godswar.Server.Application.Pets;

/// <summary>Atomic consumption and owner-fenced vital settlement proof for one HP/MP potion.</summary>
internal sealed record PotionUseEvidence(long ItemInstanceId, uint ItemTemplateId, int KitBagSlot,
    int SkillId, int AppliedAmount, PotionVitalsSnapshot Before, PotionVitalsSnapshot After,
    PlayerOwnershipFence Ownership)
{
    public bool IsValid => ItemInstanceId > 0 && ItemTemplateId > 0 && KitBagSlot is >= 0 and < 96 &&
        Ownership.IsValid && BagConsumableEffectCatalog.TryResolve(SkillId, out var effect) &&
        PotionUsePolicy.TryPlan(Before, effect, out var plan) &&
        plan!.AppliedAmount == AppliedAmount && plan.PersistedVitals == After;

    public bool Matches(PotionUsePlan plan, uint itemTemplateId, PlayerOwnershipFence ownership) =>
        IsValid && ItemTemplateId == itemTemplateId && Ownership == ownership &&
        Before == plan.Before && After == plan.PersistedVitals &&
        SkillId == plan.Effect.SkillId && AppliedAmount == plan.AppliedAmount;
}
