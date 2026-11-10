using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Characters;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterLifecycleCommandContractChecks
{
    private static void CheckReceiptHashContracts()
    {
        var eventId = Guid.Parse("13562c1c-181a-46ad-86cf-9edb714b1d93");
        var legacy = InvalidReceiptPayload(
            CommandFamily.CharacterCreate,
            CharacterLifecycleReceiptStatus.Created,
            null,
            null,
            eventId);
        AssertReorderedReceiptVerifies(legacy, RealmId.Tempest);

        foreach (var realm in new[] { RealmId.Tempest, RealmId.Dwargon })
        {
            var receipt = new CharacterLifecycleReceipt(
                CommandFamily.CharacterCreate,
                CharacterLifecycleReceiptStatus.Created,
                347, realm, 0, 10, 1, "LifecycleHero",
                null, null, "1", eventId);
            AssertReorderedReceiptVerifies(
                CharacterLifecyclePersistenceCodec.Encode(receipt), realm);
        }

        // Retention timestamps must survive JSONB formatting with the same
        // canonical writer used when their original receipt was committed.
        var restoreUntil = new DateTimeOffset(2026, 10, 7, 4, 5, 6, TimeSpan.Zero)
            .AddTicks(1234567);
        var deleted = Receipt(
            CommandFamily.CharacterDelete,
            CharacterLifecycleReceiptStatus.Deleted,
            restoreUntil,
            restoreUntil.AddDays(7),
            eventId);
        AssertReorderedReceiptVerifies(
            CharacterLifecyclePersistenceCodec.Encode(deleted), RealmId.Tempest);
        AssertReorderedReceiptVerifies(
            InvalidReceiptPayload(deleted.Family, deleted.Status,
                deleted.RestoreUntil, deleted.PurgeAfter, deleted.OutboxEventId),
            RealmId.Tempest);
    }

    private static void AssertReorderedReceiptVerifies(byte[] encoded, RealmId realm)
    {
        var original = CharacterLifecyclePersistenceCodec.Decode(encoded);
        var hash = CharacterLifecyclePersistenceCodec.Hash(encoded);
        var reordered = ReorderReceiptJson(encoded);
        Check.True(reordered != Encoding.UTF8.GetString(encoded),
            "receipt fixture changes physical JSON representation");
        var verified = CharacterLifecyclePersistenceCodec.DecodeAndVerify(
            reordered, hash, CharacterLifecyclePersistenceCodec.ResultCode(original),
            1, original.Family, 347, realm, 0);
        Check.Equal(original.CharacterName, verified.CharacterName,
            "JSONB property reordering preserves the verified lifecycle result");
        Check.Equal(original.RealmId.Value, verified.RealmId.Value,
            "canonical hashing preserves the original contract's realm");
        Check.True(original.RestoreUntil == verified.RestoreUntil,
            "canonical hashing preserves retention timestamps");

        var changedContent = reordered.Replace("LifecycleHero", "DifferentHero", StringComparison.Ordinal);
        Check.Throws<InvalidDataException>(
            () => CharacterLifecyclePersistenceCodec.DecodeAndVerify(
                changedContent, hash, CharacterLifecyclePersistenceCodec.ResultCode(original),
                1, original.Family, 347, realm, 0),
            "canonicalization does not accept changed lifecycle receipt content");
        var changedHash = hash.ToArray();
        changedHash[0] ^= 1;
        Check.Throws<InvalidDataException>(
            () => CharacterLifecyclePersistenceCodec.DecodeAndVerify(
                reordered, changedHash, CharacterLifecyclePersistenceCodec.ResultCode(original),
                1, original.Family, 347, realm, 0),
            "canonicalization does not accept a changed stored hash");
        Check.Throws<InvalidDataException>(
            () => CharacterLifecyclePersistenceCodec.DecodeAndVerify(
                reordered, hash, CharacterLifecyclePersistenceCodec.ResultCode(original),
                2, original.Family, 347, realm, 0),
            "receipt replay still binds its authoritative audit identity");
    }

    private static string ReorderReceiptJson(byte[] encoded)
    {
        using var document = JsonDocument.Parse(encoded);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()
                         .OrderByDescending(static property => property.Name, StringComparer.Ordinal))
            {
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
