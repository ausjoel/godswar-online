using Godswar.Server.State;

namespace Godswar.Server.Application.Pets;

// The amount is checked by arithmetic, not against the policy, so stored receipts stay valid if a reward is retuned.
internal sealed record ProgressItemEvidence(long ItemInstanceId, uint ItemTemplateId, int KitBagSlot,
    ProgressItemKind Kind, int Amount, int PreviousBalance, int NewBalance,
    long PreviousRevision, long NewRevision)
{
    public bool IsValid => ItemInstanceId > 0 && ItemTemplateId > 0 && KitBagSlot is >= 0 and < 96 &&
        Enum.IsDefined(Kind) && Amount > 0 && PreviousBalance >= 0 &&
        (long)PreviousBalance + Amount <= int.MaxValue && NewBalance == (long)PreviousBalance + Amount &&
        PreviousRevision is >= 0 and < long.MaxValue && NewRevision == PreviousRevision + 1;
}
