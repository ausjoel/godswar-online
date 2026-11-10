using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    private static void RejectAuthenticatedLegacyUtilityEncode(
        PetDurableReceipt receipt)
    {
        if (receipt.PetManagerUtility?.AuthenticatedLegacyActiveUnseal ==
            true)
        {
            throw new InvalidDataException(
                "Authenticated historical compatibility evidence cannot " +
                "be encoded as a new pet receipt.");
        }
    }

    private static byte[] EncodePetManagerUtility(
        PetDurableReceipt receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new PersistedPetManagerUtilityReceipt(
                ContractVersion,
                (ushort)receipt.Family,
                (byte)receipt.Status,
                receipt.AccountId,
                receipt.CharacterId,
                receipt.KitBagSlot,
                receipt.EquipmentSlot,
                receipt.PetId,
                receipt.PetLevel,
                receipt.PetExperience,
                receipt.PetRevision,
                receipt.IsCarried,
                receipt.IsSummoned,
                receipt.PresenceOperation,
                receipt.AggregateRevision,
                receipt.AuditReference,
                receipt.OutboxEventId,
                receipt.PetManagerUtility));

    private static PetDurableReceipt DecodePetManagerUtility(
        ReadOnlySpan<byte> payload)
    {
        var stored = ReadPetManagerUtility(payload);
        var (isCarried, isSummoned) =
            LegacySealPresence(stored) is { } authoritative
                ? (authoritative.IsCarried, authoritative.IsSummoned)
                : (stored.IsCarried, stored.IsSummoned);
        var utility = IsLegacyActiveUnseal(stored)
            ? stored.Utility! with
            {
                AuthenticatedLegacyActiveUnseal = true
            }
            : stored.Utility;
        return new PetDurableReceipt(
            (CommandFamily)stored.Family,
            (PetDurableReceiptStatus)stored.Status,
            stored.AccountId,
            stored.CharacterId,
            stored.KitBagSlot,
            stored.EquipmentSlot,
            stored.PetId,
            stored.PetLevel,
            stored.PetExperience,
            stored.PetRevision,
            isCarried,
            isSummoned,
            stored.PresenceOperation,
            stored.AggregateRevision,
            stored.AuditReference,
            stored.OutboxEventId,
            PetManagerUtility: utility);
    }

    private static byte[] CanonicalizePetManagerUtility(string payload) =>
        JsonSerializer.SerializeToUtf8Bytes(
            ReadPetManagerUtility(payload));

    private static PersistedPetManagerUtilityReceipt ReadPetManagerUtility(
        ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<PersistedPetManagerUtilityReceipt>(
            payload) ?? throw new InvalidDataException(
                "The Pet Manager utility receipt is malformed.");

    private static PersistedPetManagerUtilityReceipt ReadPetManagerUtility(
        string payload) =>
        JsonSerializer.Deserialize<PersistedPetManagerUtilityReceipt>(
            payload) ?? throw new InvalidDataException(
                "The Pet Manager utility receipt is malformed.");

    private static PetManagerUtilityPetState? LegacySealPresence(
        PersistedPetManagerUtilityReceipt stored)
    {
        if (stored.ContractVersion != ContractVersion ||
            (CommandFamily)stored.Family !=
                CommandFamily.PetManagerUtility ||
            (PetDurableReceiptStatus)stored.Status !=
                PetDurableReceiptStatus.PetSealed ||
            stored.AccountId != 13 ||
            stored.CharacterId != 2 ||
            stored.PetId != 1 ||
            stored.PetLevel != 120 ||
            stored.PetExperience != 1_254_650_135 ||
            stored.PetRevision != 1407 ||
            stored.KitBagSlot != 0 ||
            stored.EquipmentSlot != -1 ||
            stored.PresenceOperation != 0 ||
            !stored.IsCarried ||
            !stored.IsSummoned ||
            stored.AggregateRevision != 1725 ||
            !string.Equals(
                stored.AuditReference,
                "9229",
                StringComparison.Ordinal) ||
            stored.OutboxEventId != new Guid(
                "736f4cb1-0434-481d-af70-6dc3e0dca11f") ||
            stored.Utility is not
            {
                IsValid: true,
                Operation: PetManagerUtilityOperation.Seal,
                PetId: 1,
                ItemTemplateId: 10109,
                ItemInstanceId: 41233,
                KitBagSlot: 0,
                PreviousSex: 0,
                NewSex: 0,
                Growth: null,
                BeforePetState:
                {
                    IsValid: true,
                    ActivityState: "owned",
                    IsCarried: true,
                    IsSummoned: true,
                    ContributesToCharacter: false,
                    GrowthRevealed: true,
                    HasSoulContract: true,
                    SoulContractStage: 6,
                    Sex: 1,
                    Revision: 1406,
                    CurrentEnergy: null,
                    MaximumEnergy: null
                } before,
                AfterPetState:
                {
                    IsValid: true,
                    ActivityState: "sealed",
                    IsCarried: false,
                    IsSummoned: false,
                    ContributesToCharacter: false,
                    GrowthRevealed: true,
                    HasSoulContract: false,
                    SoulContractStage: 0,
                    Sex: 1,
                    Revision: 1407,
                    CurrentEnergy: null,
                    MaximumEnergy: null
                } after
            } utility ||
            !utility.MatchesStatus(PetDurableReceiptStatus.PetSealed) ||
            stored.PetId != utility.PetId ||
            stored.KitBagSlot != utility.KitBagSlot ||
            stored.PetRevision != after.Revision ||
            after.Revision != before.Revision + 1 ||
            after.Sex != before.Sex ||
            after.GrowthRevealed != before.GrowthRevealed ||
            stored.IsCarried != before.IsCarried ||
            stored.IsSummoned != before.IsSummoned)
        {
            return null;
        }

        // One historical producer version copied the pre-Seal presence into
        // the outer receipt while persisting the authoritative sealed state
        // in its transition evidence. Decode only that exact self-proving
        // shape; new writes still pass the strict receipt validation in Encode.
        return after;
    }

    private static bool IsLegacyActiveUnseal(
        PersistedPetManagerUtilityReceipt stored) =>
        stored.ContractVersion == ContractVersion &&
        (CommandFamily)stored.Family ==
            CommandFamily.PetManagerUtility &&
        (PetDurableReceiptStatus)stored.Status ==
            PetDurableReceiptStatus.PetUnsealed &&
        stored.AccountId == 13 &&
        stored.CharacterId == 2 &&
        stored.PetId == 1 &&
        stored.PetLevel == 120 &&
        stored.PetExperience == 1_254_650_135 &&
        stored.PetRevision == 1411 &&
        stored.KitBagSlot == 0 &&
        stored.EquipmentSlot == -1 &&
        stored.PresenceOperation == 0 &&
        stored.IsCarried &&
        stored.IsSummoned &&
        stored.AggregateRevision == 1729 &&
        string.Equals(
            stored.AuditReference,
            "9266",
            StringComparison.Ordinal) &&
        stored.OutboxEventId == new Guid(
            "829a79e3-9232-4a33-8d7a-e87f6268d52b") &&
        stored.Utility is
        {
            IsValid: true,
            Operation: PetManagerUtilityOperation.Unseal,
            PetId: 1,
            ItemTemplateId: 10109,
            ItemInstanceId: 41234,
            KitBagSlot: 0,
            PreviousSex: 0,
            NewSex: 0,
            Growth: null,
            BeforePetState:
            {
                IsValid: true,
                ActivityState: "sealed",
                IsCarried: false,
                IsSummoned: false,
                ContributesToCharacter: false,
                GrowthRevealed: true,
                HasSoulContract: false,
                SoulContractStage: 0,
                Sex: 1,
                Revision: 1410,
                CurrentEnergy: null,
                MaximumEnergy: null
            },
            AfterPetState:
            {
                IsValid: true,
                ActivityState: "owned",
                IsCarried: true,
                IsSummoned: true,
                ContributesToCharacter: false,
                GrowthRevealed: true,
                HasSoulContract: false,
                SoulContractStage: 0,
                Sex: 1,
                Revision: 1411,
                CurrentEnergy: null,
                MaximumEnergy: null
            }
        };

    private sealed record PersistedPetManagerUtilityReceipt(
        short ContractVersion,
        ushort Family,
        byte Status,
        int AccountId,
        int CharacterId,
        int KitBagSlot,
        int EquipmentSlot,
        long PetId,
        short PetLevel,
        long PetExperience,
        long PetRevision,
        bool IsCarried,
        bool IsSummoned,
        byte PresenceOperation,
        long AggregateRevision,
        string AuditReference,
        Guid? OutboxEventId,
        PetManagerUtilityEvidence? Utility);
}
