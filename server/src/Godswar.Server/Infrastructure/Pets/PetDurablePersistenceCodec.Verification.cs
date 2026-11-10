using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    public static PetDurableReceipt DecodeAndVerify(
        string payload,
        ReadOnlySpan<byte> expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var receipt = Decode(Encoding.UTF8.GetBytes(payload));
        var header = JsonSerializer.Deserialize<PersistedReceiptHeader>(
            payload) ?? throw new InvalidDataException(
                "The pet durable receipt is malformed.");
        var canonical = (receipt.Family, header.ContractVersion) switch
        {
            (CommandFamily.BagItemActivation, ContractVersion) or
            (CommandFamily.PetGrowthReset, ContractVersion) or
            (CommandFamily.PetRebirth, ContractVersion) =>
                EncodeV1(receipt),
            (CommandFamily.BagItemActivation,
                PreviousBagItemActivationContractVersion) =>
                EncodeBagItemActivationV2(receipt),
            (CommandFamily.BagItemActivation,
                PreviousBagItemActivationContractVersionV3) =>
                EncodeBagItemActivationV3(receipt),
            (CommandFamily.BagItemActivation,
                PreviousBagItemActivationContractVersionV5) =>
                EncodeBagItemActivationV5(receipt),
            (CommandFamily.BagItemActivation,
                PreviousBagItemActivationContractVersionV7) =>
                EncodeBagItemActivationV7(receipt),
            (CommandFamily.BagItemActivation,
                PreviousBagItemActivationContractVersionV6) =>
                EncodeBagItemActivationV6(receipt),
            (CommandFamily.BagItemActivation,
                PreviousBagItemActivationContractVersionV4) =>
                EncodeBagItemActivationV4(receipt),
            (CommandFamily.PetGrowthReset,
                PreviousPetGrowthResetContractVersion) =>
                EncodePetGrowthV4(receipt),
            (CommandFamily.PetGrowthReset,
                LegacyPetGrowthResetContractVersion) =>
                EncodePetGrowthV3(receipt),
            (CommandFamily.PetManagerUtility, ContractVersion) =>
                CanonicalizePetManagerUtility(payload),
            _ => Encode(receipt)
        };
        var hash = SHA256.HashData(canonical);
        if (expectedHash.Length != hash.Length ||
            !CryptographicOperations.FixedTimeEquals(hash, expectedHash))
        {
            throw new InvalidDataException(
                "The pet durable receipt hash is invalid.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static PersistedReceiptHeader ReadHeader(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The pet durable receipt has an invalid size.");
        }

        return JsonSerializer.Deserialize<PersistedReceiptHeader>(payload) ??
            throw new InvalidDataException(
                "The pet durable receipt is malformed.");
    }

    private static byte[] EncodeV1(PetDurableReceipt receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new PersistedReceipt(
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
                receipt.OutboxEventId));

    private sealed record PersistedReceipt(
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
        Guid? OutboxEventId);

    private sealed record PersistedReceiptHeader(
        short ContractVersion,
        ushort Family);
}
