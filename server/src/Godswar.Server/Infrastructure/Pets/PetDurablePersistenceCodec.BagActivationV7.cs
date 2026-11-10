using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    private static byte[] EncodeBagItemActivationV7(PetDurableReceipt receipt)
    {
        if (receipt.Status == PetDurableReceiptStatus.EggHatched && receipt.HatchRank is null)
            throw new InvalidDataException("A new pet hatch receipt must retain rank evidence.");
        return JsonSerializer.SerializeToUtf8Bytes(new PersistedBagItemActivationReceiptV7(
            PreviousBagItemActivationContractVersionV7, (ushort)receipt.Family, (byte)receipt.Status,
            receipt.AccountId, receipt.CharacterId, receipt.KitBagSlot, receipt.EquipmentSlot,
            receipt.PetId, receipt.PetLevel, receipt.PetExperience, receipt.PetRevision,
            receipt.IsCarried, receipt.IsSummoned, receipt.PresenceOperation, receipt.AggregateRevision,
            receipt.AuditReference, receipt.OutboxEventId, receipt.HatchRank, receipt.SkillLearn,
            receipt.PlayerSkillLearn, receipt.WonderlandSack, receipt.PlayerExperience,
            receipt.CareRestore, receipt.MoneyBag, receipt.Potion));
    }

    private static PetDurableReceipt DecodeBagItemActivationV7(ReadOnlySpan<byte> payload)
    {
        var stored = JsonSerializer.Deserialize<PersistedBagItemActivationReceiptV7>(payload) ??
            throw new InvalidDataException("The v7 bag-activation receipt is malformed.");
        if ((PetDurableReceiptStatus)stored.Status == PetDurableReceiptStatus.EggHatched && stored.HatchRank is null)
            throw new InvalidDataException("The pet hatch receipt omitted rank evidence.");
        return new((CommandFamily)stored.Family, (PetDurableReceiptStatus)stored.Status,
            stored.AccountId, stored.CharacterId, stored.KitBagSlot, stored.EquipmentSlot,
            stored.PetId, stored.PetLevel, stored.PetExperience, stored.PetRevision,
            stored.IsCarried, stored.IsSummoned, stored.PresenceOperation, stored.AggregateRevision,
            stored.AuditReference, stored.OutboxEventId, HatchRank: stored.HatchRank,
            SkillLearn: stored.SkillLearn, PlayerSkillLearn: stored.PlayerSkillLearn,
            WonderlandSack: stored.WonderlandSack, PlayerExperience: stored.PlayerExperience,
            CareRestore: stored.CareRestore, MoneyBag: stored.MoneyBag, Potion: stored.Potion);
    }

    // Version 7 adds potion settlement evidence. Historical v5/v6 encodings
    // retain their exact canonical serialization and hash verification.
    private sealed record PersistedBagItemActivationReceiptV7(
        short ContractVersion, ushort Family, byte Status, int AccountId, int CharacterId,
        int KitBagSlot, int EquipmentSlot, long PetId, short PetLevel, long PetExperience,
        long PetRevision, bool IsCarried, bool IsSummoned, byte PresenceOperation,
        long AggregateRevision, string AuditReference, Guid? OutboxEventId,
        PetHatchRankEvidence? HatchRank, PetSkillLearnEvidence? SkillLearn,
        PlayerSkillLearnEvidence? PlayerSkillLearn, WonderlandSackOpenEvidence? WonderlandSack,
        PlayerExperienceItemEvidence? PlayerExperience,
        PetCareRestoreEvidence? CareRestore = null,
        MoneyBagOpenEvidence? MoneyBag = null,
        PotionUseEvidence? Potion = null);
}
