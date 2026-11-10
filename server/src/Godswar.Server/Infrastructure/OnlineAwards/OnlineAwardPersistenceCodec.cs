using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.OnlineAwards;

namespace Godswar.Server.Infrastructure.OnlineAwards;

internal static class OnlineAwardPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string PrincipalType = "account";
    public const string AggregateType = "online-award";
    public const string CommandFamily = "online_award";
    public const string ConsumerKey = "online-award-projection";
    public const string EventType = "online-award.claimed";
    public const string ResultCode = "committed";
    public const string RetentionPolicy = "permanent";
    public const string OrderingPolicy = "strict";
    public const int MaximumPayloadBytes =
        OutboxEventMessage.MaximumPayloadBytes;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string AggregateKey(int characterId) =>
        characterId > 0
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"character:{characterId}:online-award")
            : throw new ArgumentOutOfRangeException(nameof(characterId));

    public static byte[] Encode(OnlineAwardExecutionReceipt receipt)
    {
        Validate(receipt);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            receipt,
            JsonOptions);
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The Online Award receipt exceeds its payload bound.");
        }
        return payload;
    }

    public static OnlineAwardExecutionReceipt Decode(ReadOnlySpan<byte> value)
    {
        if (value.Length is <= 0 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored Online Award payload has an invalid size.");
        }
        try
        {
            var receipt = JsonSerializer.Deserialize<
                OnlineAwardExecutionReceipt>(
                value,
                JsonOptions) ?? throw new InvalidDataException(
                    "The stored Online Award receipt is null.");
            Validate(receipt);
            return receipt;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException or NotSupportedException or
            ArgumentException or OverflowException)
        {
            throw new InvalidDataException(
                "The stored Online Award receipt is malformed.",
                exception);
        }
    }

    public static OnlineAwardExecutionReceipt DecodeAndVerify(
        string payload,
        byte[] expectedHash,
        long expectedAuditId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var receipt = Decode(System.Text.Encoding.UTF8.GetBytes(payload));
        var actualHash = Hash(Encode(receipt));
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The Online Award receipt hash is invalid.");
        }

        if (!string.Equals(
                receipt.AuditId,
                expectedAuditId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Online Award audit reference is invalid.");
        }
        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> value) =>
        SHA256.HashData(value);

    private static void Validate(OnlineAwardExecutionReceipt? receipt)
    {
        if (receipt is null ||
            receipt.CharacterId <= 0 || receipt.RealmId <= 0 ||
            receipt.NativeResultSubId !=
                Godswar.Server.Domain.World.Content.OnlineAwardProtocol
                    .SuccessSubId ||
            receipt.BalanceRevision <= 0 ||
            !IsDigest(receipt.BalanceSha256) ||
            !IsDigest(receipt.ItemContentRevision) ||
            receipt.ClaimDay == default ||
            receipt.ItemDeltas is null ||
            receipt.ItemDeltas.Count is < 1 or >
                OnlineAwardBalanceSnapshot.MaximumRewardRows ||
            receipt.ItemDeltas.Any(static item =>
                item.ItemId <= 0 || item.ItemQuality is < 1 or > 16 ||
                item.Bound is < 0 or > 1 ||
                item.Quantity is < 1 or >
                    OnlineAwardBalanceSnapshot.MaximumTotalQuantity) ||
            receipt.ItemDeltas.Sum(static item => item.Quantity) >
                OnlineAwardBalanceSnapshot.MaximumTotalQuantity ||
            receipt.InventoryRevision <= 0 ||
            receipt.OnlineAwardRevision <= 0 ||
            !long.TryParse(
                receipt.AuditId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var auditId) ||
            auditId <= 0 ||
            receipt.EventId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The Online Award receipt is invalid.");
        }
    }

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
