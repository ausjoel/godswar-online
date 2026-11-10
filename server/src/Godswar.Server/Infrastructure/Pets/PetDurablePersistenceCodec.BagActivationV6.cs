using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    private static byte[] EncodeBagItemActivationV6(PetDurableReceipt receipt)
    {
        if (receipt.Status == PetDurableReceiptStatus.EggHatched && receipt.HatchRank is null)
            throw new InvalidDataException("A new pet hatch receipt must retain rank evidence.");
        return JsonSerializer.SerializeToUtf8Bytes(new PersistedBagItemActivationReceiptV6(
            PreviousBagItemActivationContractVersionV6, (ushort)receipt.Family, (byte)receipt.Status,
            receipt.AccountId, receipt.CharacterId, receipt.KitBagSlot, receipt.EquipmentSlot,
            receipt.PetId, receipt.PetLevel, receipt.PetExperience, receipt.PetRevision,
            receipt.IsCarried, receipt.IsSummoned, receipt.PresenceOperation, receipt.AggregateRevision,
            receipt.AuditReference, receipt.OutboxEventId, receipt.HatchRank, receipt.SkillLearn,
            receipt.PlayerSkillLearn, receipt.WonderlandSack, receipt.PlayerExperience,
            receipt.CareRestore, receipt.MoneyBag));
    }

    private static PetDurableReceipt DecodeBagItemActivationV6(ReadOnlySpan<byte> payload)
    {
        var stored = JsonSerializer.Deserialize<PersistedBagItemActivationReceiptV6>(payload) ??
            throw new InvalidDataException("The v6 bag-activation receipt is malformed.");
        if ((PetDurableReceiptStatus)stored.Status == PetDurableReceiptStatus.EggHatched && stored.HatchRank is null)
            throw new InvalidDataException("The pet hatch receipt omitted rank evidence.");
        return new((CommandFamily)stored.Family, (PetDurableReceiptStatus)stored.Status,
            stored.AccountId, stored.CharacterId, stored.KitBagSlot, stored.EquipmentSlot,
            stored.PetId, stored.PetLevel, stored.PetExperience, stored.PetRevision,
            stored.IsCarried, stored.IsSummoned, stored.PresenceOperation, stored.AggregateRevision,
            stored.AuditReference, stored.OutboxEventId, HatchRank: stored.HatchRank,
            SkillLearn: stored.SkillLearn, PlayerSkillLearn: stored.PlayerSkillLearn,
            WonderlandSack: stored.WonderlandSack, PlayerExperience: stored.PlayerExperience,
            CareRestore: stored.CareRestore, MoneyBag: stored.MoneyBag);
    }

    // Version 6 adds money-bag settlement evidence. Version 5 retains its own
    // exact serialization for canonical verification of historical receipts.
    private sealed record PersistedBagItemActivationReceiptV6(
        short ContractVersion, ushort Family, byte Status, int AccountId, int CharacterId,
        int KitBagSlot, int EquipmentSlot, long PetId, short PetLevel, long PetExperience,
        long PetRevision, bool IsCarried, bool IsSummoned, byte PresenceOperation,
        long AggregateRevision, string AuditReference, Guid? OutboxEventId,
        PetHatchRankEvidence? HatchRank, PetSkillLearnEvidence? SkillLearn,
        PlayerSkillLearnEvidence? PlayerSkillLearn, WonderlandSackOpenEvidence? WonderlandSack,
        PlayerExperienceItemEvidence? PlayerExperience,
        PetCareRestoreEvidence? CareRestore = null,
        MoneyBagOpenEvidence? MoneyBag = null);
}
