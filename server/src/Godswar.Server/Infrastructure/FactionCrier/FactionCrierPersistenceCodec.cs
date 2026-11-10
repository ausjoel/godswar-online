using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.FactionCrier;

internal static class FactionCrierPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ResultCode = "committed";
    public const string ConsumerKey = "faction_crier_projection_v1";
    public const string AggregateType = "character_faction_crier";
    public const string EventType = "faction_crier.operation_settled";
    public const string OrderingPolicy = "strict";
    public const string CommandFamily = "faction_crier";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    public const string ItemContentRevision = "faction-crier-nameplates-v1";
    public const int MaximumPayloadBytes =
        OutboxEventMessage.MaximumPayloadBytes;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string AggregateKey(int characterId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:faction-crier");
    }

    public static byte[] Encode(FactionCrierExecutionReceipt receipt)
    {
        Validate(receipt);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            receipt,
            SerializerOptions);
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The Faction Crier receipt exceeds its payload bound.");
        }
        return payload;
    }

    public static FactionCrierExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored Faction Crier payload has an invalid size.");
        }

        try
        {
            var receipt = JsonSerializer.Deserialize<FactionCrierExecutionReceipt>(
                payload,
                SerializerOptions) ??
                throw new InvalidDataException(
                    "The stored Faction Crier receipt is null.");
            Validate(receipt);
            return receipt;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            NotSupportedException or
            ArgumentException or
            OverflowException)
        {
            throw new InvalidDataException(
                "The stored Faction Crier receipt is malformed.",
                exception);
        }
    }

    public static FactionCrierExecutionReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        long expectedAuditId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var payload = System.Text.Encoding.UTF8.GetBytes(payloadJson);
        var receipt = Decode(payload);
        var canonical = Encode(receipt);
        var actualHash = Hash(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                expectedHash,
                actualHash))
        {
            throw new InvalidDataException(
                "The stored Faction Crier receipt hash is invalid.");
        }

        if (!string.Equals(
                receipt.AuditId,
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored Faction Crier audit identity is invalid.");
        }
        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void Validate(FactionCrierExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.CharacterId <= 0 ||
            receipt.RealmId <= 0 ||
            !Enum.IsDefined(receipt.Operation) ||
            !FactionCrierCommandEnvelope.IsMutationSubId(receipt.SubId) ||
            receipt.NativeResultSubId <= 0 ||
            receipt.PreviousLevel is < 1 or > 200 ||
            receipt.CurrentLevel < receipt.PreviousLevel ||
            receipt.CurrentLevel > 200 ||
            receipt.PreviousExperience is < 0 or > uint.MaxValue ||
            receipt.CurrentExperience is < 0 or > uint.MaxValue ||
            receipt.AwardedExperience < 0 ||
            receipt.PreviousTalentPoints < 0 ||
            receipt.CurrentTalentPoints < receipt.PreviousTalentPoints ||
            receipt.AwardedTalentPoints < 0 ||
            receipt.WalletRevision < 0 ||
            receipt.InventoryRevision < 0 ||
            receipt.ProgressionRevision < 0 ||
            receipt.FactionCrierRevision <= 0 ||
            receipt.EventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(receipt.AuditId) ||
            receipt.LevelUps is null ||
            receipt.LevelUps.Any(levelUp =>
                levelUp.Level <= receipt.PreviousLevel ||
                levelUp.Level > receipt.CurrentLevel ||
                levelUp.CurrentExperience is < 0 or > uint.MaxValue ||
                levelUp.NextLevelExperience < 0))
        {
            throw new InvalidDataException(
                "The Faction Crier receipt is inconsistent.");
        }
    }
}
