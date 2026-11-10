using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCapitalVendorItemMigrationChecks
{
    private const string MigrationId =
        "20260831_126_capital_vendor_items";

    private static readonly HashSet<int> PreexistingAdvertisedItemIds =
    [
        // Pet food/capture and reviewed pet-skill content.
        10001, 10084, 10464, 10510, 10530, 10590, 10700,

        // Equipment, starter potion, Holy Suit, portal books, mounts, and
        // reviewed Pet Manager content already published by earlier sources.
        3100, 4030, 9010, 9011, 9012, 9020, 9021, 9022, 9023,
        5800, 5801, 5802, 5804, 10112, 14220, 14460, 16170
    ];

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id == MigrationId);
        var insertedIds = Regex.Matches(
                migration.Sql,
                @"(?m)^\s*\((?<id>\d+),\s*'")
            .Select(static match =>
                int.Parse(match.Groups["id"].Value))
            .ToHashSet();

        var petIds = ReadCatalogItemIds(
            CapitalNpcServiceKind.PetMerchant,
            npcId: 5086);
        var propsIds = ReadCatalogItemIds(
            CapitalNpcServiceKind.PropsVendor,
            npcId: 5033);
        var expectedInsertedIds = petIds
            .Concat(propsIds)
            .Where(id => !PreexistingAdvertisedItemIds.Contains(id))
            .ToHashSet();

        Check.True(
            petIds.Count == 78 &&
            propsIds.Count == 33 &&
            expectedInsertedIds.Count == 87 &&
            insertedIds.SetEquals(expectedInsertedIds),
            "the migration fills every previously missing Pet Merchant and " +
            "Props Aglaia item identity");

        var skillIds = ReadCatalogItemIds(
            CapitalNpcServiceKind.SkillVendor,
            npcId: 44345);
        var publishedSkillBookIds = SkillTalentSeeds.SkillBooks
            .Select(static book => book.ItemId)
            .ToHashSet();
        Check.True(
            skillIds.Count == 86 &&
            skillIds.IsSubsetOf(publishedSkillBookIds),
            "every Skill Ajax listing is already backed by the generated " +
            "stock skill-book baseline");

        Check.True(
            migration.Sql.Split(
                "ON CONFLICT (id) DO NOTHING;",
                StringSplitOptions.None).Length == 5 &&
            !migration.Sql.Contains(
                "DO UPDATE",
                StringComparison.OrdinalIgnoreCase) &&
            !migration.Sql.Contains(
                "DELETE ",
                StringComparison.OrdinalIgnoreCase),
            "capital vendor seeding preserves every existing item template");
        Check.True(
            migration.Sql.Contains(
                "6D2BA8D3FF21A6783AB6D0E21B50963A7DB214690F7B759B9EC495808B990701",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "C90A90679EB4DF4633E0388BB4CBC318C30B0705505EE75344191DCFD4FCCF9E",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'Overlap', '99'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "\"BindType\":\"1\"",
                StringComparison.Ordinal),
            "the seeded rows retain traced client provenance, stack caps, " +
            "and binding policy");
        return Task.CompletedTask;
    }

    private static HashSet<int> ReadCatalogItemIds(
        CapitalNpcServiceKind service,
        uint npcId)
    {
        const int headerLength = 16;
        const int recordLength = 88;
        var catalog = PacketBuilder.CapitalNpcShopCatalog(
            npcId,
            new CapitalNpcShopBalances(1, 1, 1),
            service);
        var ids = new HashSet<int>();
        var offset = 0;
        while (offset < catalog.Length)
        {
            var frameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                catalog.AsSpan(offset, sizeof(ushort)));
            var count = catalog[offset + 10];
            for (var index = 0; index < count; index++)
            {
                ids.Add(checked((int)
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        catalog.AsSpan(
                            offset + headerLength + (index * recordLength),
                            sizeof(uint)))));
            }
            offset += frameLength;
        }
        return ids;
    }
}
