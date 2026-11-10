using Godswar.Server.State;

namespace Godswar.Server.Application.Pets;

internal sealed record MoneyBagOpenEvidence(long ItemInstanceId, uint ItemTemplateId, int KitBagSlot,
    int SkillId, int Amount, int PreviousSilver, int NewSilver,
    long PreviousWalletRevision, long NewWalletRevision)
{
    public bool IsValid => ItemInstanceId > 0 && ItemTemplateId > 0 && KitBagSlot is >= 0 and < 96 &&
        BagConsumableEffectCatalog.TryResolve(SkillId, out var effect) &&
        effect.Kind == BagConsumableEffectKind.GrantSilver && effect.Amount == Amount && Amount > 0 &&
        PreviousSilver >= 0 && (long)PreviousSilver + Amount <= int.MaxValue &&
        NewSilver == (long)PreviousSilver + Amount &&
        PreviousWalletRevision is >= 0 and < long.MaxValue && NewWalletRevision == PreviousWalletRevision + 1;
}
