using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static class PetManagerUtilityLegacySealCompatibilityChecks
{
    public const string CheckName =
        "Historical Pet Manager Seal presence compatibility";

    private const string CapturedPayload =
        """{"PetId": 1, "Family": 55, "Status": 81, "Utility": {"PetId": 1, "Growth": null, "NewSex": 0, "IsValid": true, "Operation": 2, "KitBagSlot": 0, "PreviousSex": 0, "AfterPetState": {"Sex": 1, "IsValid": true, "Revision": 1407, "IsCarried": false, "IsSummoned": false, "ActivityState": "sealed", "GrowthRevealed": true, "HasSoulContract": false, "SoulContractStage": 0, "ContributesToCharacter": false}, "BeforePetState": {"Sex": 1, "IsValid": true, "Revision": 1406, "IsCarried": true, "IsSummoned": true, "ActivityState": "owned", "GrowthRevealed": true, "HasSoulContract": true, "SoulContractStage": 6, "ContributesToCharacter": false}, "ItemInstanceId": 41233, "ItemTemplateId": 10109}, "PetLevel": 120, "AccountId": 13, "IsCarried": true, "IsSummoned": true, "KitBagSlot": 0, "CharacterId": 2, "PetRevision": 1407, "EquipmentSlot": -1, "OutboxEventId": "736f4cb1-0434-481d-af70-6dc3e0dca11f", "PetExperience": 1254650135, "AuditReference": "9229", "ContractVersion": 1, "AggregateRevision": 1725, "PresenceOperation": 0}""";

    private const string CapturedPayloadHash =
        "82d2041e0fae62d7647935cd82aef7c5e82abb640b96cb4d0fa9c07501db93bf";
    private const string CapturedResultHash =
        "e30466b58a4e6c612943f28ef7c5b86e95de465a1d98ec10bff1967a85582af9";
    private static readonly Guid CapturedEventId =
        Guid.Parse("736f4cb1-0434-481d-af70-6dc3e0dca11f");

    public static async Task RunAsync()
    {
        var payload = Encoding.UTF8.GetBytes(CapturedPayload);
        Check.Equal(
            CapturedPayloadHash,
            Convert.ToHexString(PetDurablePersistenceCodec.Hash(payload))
                .ToLowerInvariant(),
            "captured v1725 JSONB payload text is exact");

        var decoded = PetDurablePersistenceCodec.Decode(payload);
        AssertNormalized(decoded, "outbox Decode");
        var verified = PetDurablePersistenceCodec.DecodeAndVerify(
            CapturedPayload,
            Convert.FromHexString(CapturedResultHash));
        AssertNormalized(verified, "immutable inbox hash verification");
        Check.Equal(
            decoded,
            verified,
            "outbox and inbox paths return the same normalized receipt");

        await new PetDurableOutboxConsumer().ConsumeAsync(
            new OutboxEventMessage(
                CapturedEventId,
                PetDurablePersistenceCodec.ConsumerKey,
                PetDurablePersistenceCodec.AggregateType,
                PetDurablePersistenceCodec.AggregateKey(2),
                aggregateRevision: 1725,
                PetDurablePersistenceCodec.EventType(decoded.Family),
                schemaVersion: 1,
                new DateTimeOffset(
                    2026, 8, 13, 21, 22, 9, TimeSpan.Zero),
                payload));

        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(
                decoded with
                {
                    IsCarried = true,
                    IsSummoned = true
                }),
            "new Encode rejects the historical stale outer presence");
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.DecodeAndVerify(
                CapturedPayload,
                new byte[32]),
            "legacy normalization cannot bypass the immutable result hash");

        CheckNearMissesFailClosed();
    }

    private static void AssertNormalized(
        PetDurableReceipt receipt,
        string path)
    {
        var utility = receipt.PetManagerUtility ??
            throw new InvalidDataException("Utility evidence is absent.");
        Check.True(
            receipt.Family ==
                Godswar.Server.Application.Commands.CommandFamily
                    .PetManagerUtility &&
            receipt.Status == PetDurableReceiptStatus.PetSealed &&
            receipt.OutboxEventId == CapturedEventId &&
            receipt.AggregateRevision == 1725 &&
            receipt.PetRevision == 1407 &&
            !receipt.IsCarried &&
            !receipt.IsSummoned &&
            utility.BeforePetState is
                {
                    ActivityState: "owned",
                    IsCarried: true,
                    IsSummoned: true,
                    Revision: 1406
                } &&
            utility.AfterPetState is
                {
                    ActivityState: "sealed",
                    IsCarried: false,
                    IsSummoned: false,
                    Revision: 1407
                },
            $"v1725 {path} preserves evidence and uses authoritative After presence");
    }

    private static void CheckNearMissesFailClosed()
    {
        CheckDecodeRejected(
            root => root["IsCarried"] = false,
            "outer presence not exactly equal to Before");
        CheckDecodeRejected(
            root => root["KitBagSlot"] = 1,
            "outer slot not equal to utility evidence");
        CheckDecodeRejected(
            root => root["AggregateRevision"] = 1726,
            "aggregate identity not exact");
        CheckDecodeRejected(
            root => root["CharacterId"] = 3,
            "character identity not exact");
        CheckDecodeRejected(
            root => root["OutboxEventId"] =
                "736f4cb1-0434-481d-af70-6dc3e0dca120",
            "event identity not exact");
        CheckDecodeRejected(
            root => root["AuditReference"] = "9230",
            "audit identity not exact");
        CheckDecodeRejected(
            root => State(root, "BeforePetState")["ActivityState"] =
                "sealed",
            "Before state not owned");
        CheckDecodeRejected(
            root => root["Utility"]!.AsObject()["ItemInstanceId"] = 41234,
            "item identity not exact");
        CheckDecodeRejected(
            root => State(root, "BeforePetState")["SoulContractStage"] = 5,
            "Before soul-contract evidence not exact");
        CheckDecodeRejected(
            root => State(root, "AfterPetState")["Revision"] = 1408,
            "nonconsecutive evidence revisions");
        CheckDecodeRejected(
            root => State(root, "AfterPetState")["Sex"] = 0,
            "Seal unexpectedly changing sex");
        CheckDecodeRejected(
            root => State(root, "AfterPetState")["IsSummoned"] = true,
            "After state not fully inactive");
    }

    private static JsonObject State(JsonObject root, string name) =>
        root["Utility"]!.AsObject()[name]!.AsObject();

    private static void CheckDecodeRejected(
        Action<JsonObject> mutate,
        string reason)
    {
        var root = JsonNode.Parse(CapturedPayload)!.AsObject();
        mutate(root);
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Decode(
                Encoding.UTF8.GetBytes(root.ToJsonString())),
            $"v1725 compatibility rejects a near miss: {reason}");
    }
}
