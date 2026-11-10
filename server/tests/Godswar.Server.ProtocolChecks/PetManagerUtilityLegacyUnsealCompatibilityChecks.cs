using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static class PetManagerUtilityLegacyUnsealCompatibilityChecks
{
    public const string CheckName =
        "Authenticated historical Pet Manager active Unseal";

    private const string CapturedPayload =
        """{"PetId": 1, "Family": 55, "Status": 82, "Utility": {"PetId": 1, "Growth": null, "NewSex": 0, "IsValid": true, "Operation": 3, "KitBagSlot": 0, "PreviousSex": 0, "AfterPetState": {"Sex": 1, "IsValid": true, "Revision": 1411, "IsCarried": true, "IsSummoned": true, "ActivityState": "owned", "GrowthRevealed": true, "HasSoulContract": false, "SoulContractStage": 0, "ContributesToCharacter": false}, "BeforePetState": {"Sex": 1, "IsValid": true, "Revision": 1410, "IsCarried": false, "IsSummoned": false, "ActivityState": "sealed", "GrowthRevealed": true, "HasSoulContract": false, "SoulContractStage": 0, "ContributesToCharacter": false}, "ItemInstanceId": 41234, "ItemTemplateId": 10109}, "PetLevel": 120, "AccountId": 13, "IsCarried": true, "IsSummoned": true, "KitBagSlot": 0, "CharacterId": 2, "PetRevision": 1411, "EquipmentSlot": -1, "OutboxEventId": "829a79e3-9232-4a33-8d7a-e87f6268d52b", "PetExperience": 1254650135, "AuditReference": "9266", "ContractVersion": 1, "AggregateRevision": 1729, "PresenceOperation": 0}""";

    private const string CapturedPayloadHash =
        "28e54e52e4056eeb54cbaa8ce5e329a3a93d3cc9e9b04344b2309ed40ffa99c8";
    private const string CapturedResultHash =
        "9626feb17929dde0a9440eab960c95709d6b5d0bbb304bfdbcbf9fa591f9c5ee";
    private static readonly Guid CapturedEventId =
        Guid.Parse("829a79e3-9232-4a33-8d7a-e87f6268d52b");

    public static async Task RunAsync()
    {
        var payload = Encoding.UTF8.GetBytes(CapturedPayload);
        Check.Equal(
            CapturedPayloadHash,
            Convert.ToHexString(PetDurablePersistenceCodec.Hash(payload))
                .ToLowerInvariant(),
            "captured v1729 JSONB payload text is exact");

        var decoded = PetDurablePersistenceCodec.Decode(payload);
        AssertAuthenticated(decoded, "outbox Decode");
        var verified = PetDurablePersistenceCodec.DecodeAndVerify(
            CapturedPayload,
            Convert.FromHexString(CapturedResultHash));
        AssertAuthenticated(verified, "immutable inbox hash verification");
        Check.Equal(
            decoded,
            verified,
            "v1729 outbox and inbox paths authenticate the same receipt");

        await new PetDurableOutboxConsumer().ConsumeAsync(
            new OutboxEventMessage(
                CapturedEventId,
                PetDurablePersistenceCodec.ConsumerKey,
                PetDurablePersistenceCodec.AggregateType,
                PetDurablePersistenceCodec.AggregateKey(2),
                aggregateRevision: 1729,
                PetDurablePersistenceCodec.EventType(decoded.Family),
                schemaVersion: 1,
                new DateTimeOffset(
                    2026, 8, 14, 3, 55, 3, TimeSpan.Zero),
                payload));

        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(decoded),
            "new Encode explicitly rejects authenticated legacy evidence");
        var unauthenticated = decoded with
        {
            PetManagerUtility = decoded.PetManagerUtility! with
            {
                AuthenticatedLegacyActiveUnseal = false
            }
        };
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(unauthenticated),
            "new active Unseal without energy cannot use compatibility");
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.DecodeAndVerify(
                CapturedPayload,
                new byte[32]),
            "v1729 authentication cannot bypass its immutable result hash");

        CheckModernEnergyEvidenceStillEncodes(unauthenticated);
        CheckNearMissesFailClosed();
    }

    private static void AssertAuthenticated(
        PetDurableReceipt receipt,
        string path)
    {
        var utility = receipt.PetManagerUtility ??
            throw new InvalidDataException("Utility evidence is absent.");
        Check.True(
            receipt.Status == PetDurableReceiptStatus.PetUnsealed &&
            receipt.OutboxEventId == CapturedEventId &&
            receipt.AggregateRevision == 1729 &&
            receipt.PetRevision == 1411 &&
            receipt.IsCarried &&
            receipt.IsSummoned &&
            utility.AuthenticatedLegacyActiveUnseal &&
            utility.BeforePetState is
                {
                    ActivityState: "sealed",
                    IsCarried: false,
                    IsSummoned: false,
                    CurrentEnergy: null,
                    MaximumEnergy: null,
                    Revision: 1410
                } &&
            utility.AfterPetState is
                {
                    ActivityState: "owned",
                    IsCarried: true,
                    IsSummoned: true,
                    CurrentEnergy: null,
                    MaximumEnergy: null,
                    Revision: 1411
                },
            $"v1729 {path} authenticates without inventing energy");
    }

    private static void CheckModernEnergyEvidenceStillEncodes(
        PetDurableReceipt receipt)
    {
        var utility = receipt.PetManagerUtility!;
        var withEnergy = receipt with
        {
            AuditReference = "modern-full-energy-unseal",
            OutboxEventId = Guid.NewGuid(),
            PetManagerUtility = utility with
            {
                BeforePetState = utility.BeforePetState! with
                {
                    CurrentEnergy = 37,
                    MaximumEnergy = 100
                },
                AfterPetState = utility.AfterPetState! with
                {
                    CurrentEnergy = 100,
                    MaximumEnergy = 100
                }
            }
        };
        var encoded = PetDurablePersistenceCodec.Encode(withEnergy);
        Check.Equal(
            withEnergy,
            PetDurablePersistenceCodec.Decode(encoded),
            "ordinary active Unseal remains strict and energy-authenticated");
    }

    private static void CheckNearMissesFailClosed()
    {
        CheckDecodeRejected(
            root => root["CharacterId"] = 3,
            "character identity not exact");
        CheckDecodeRejected(
            root => root["OutboxEventId"] =
                "829a79e3-9232-4a33-8d7a-e87f6268d520",
            "event identity not exact");
        CheckDecodeRejected(
            root => root["AuditReference"] = "9267",
            "audit identity not exact");
        CheckDecodeRejected(
            root => root["Utility"]!.AsObject()["ItemInstanceId"] = 41235,
            "item identity not exact");
        CheckDecodeRejected(
            root => State(root, "AfterPetState")["Revision"] = 1412,
            "transition revision not exact");
        CheckDecodeRejected(
            root => State(root, "BeforePetState")["IsSummoned"] = true,
            "sealed Before presence not exact");
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
            $"v1729 authentication rejects a near miss: {reason}");
    }
}
