using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private const string OfficialPetItemsV3Revision =
        "BCF91FCD7A9E3C5EA93B774143B5D2F9B714B147E40EBF0B85C639CF0DD63057";
    private const string OfficialPetItemsV3Source =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v3";
    private const int CaptureToolItemId = 10084;
    private const string OfficialOpalV1Revision =
        "B757C74890CA130E97CFF44DEBEF4A51D84746FD322E632A861E31261AAF90A1";

    // Measured from the controlled-host fixture: the pets-v2 predecessor plus
    // the reviewed client-catalog family (see ClientCatalogItemContentBaseline).
    private const string ClientCatalogV1Revision =
        "A60B3E609ED22A54FA261329AFEF175A6F2A9433A0730C3D94272017EF438169";

    private static async Task AssertOfficialPetItemsV3UpgradeAsync(
        NpgsqlDataSource dataSource,
        string originalRevision)
    {
        var original = await PostgresItemTemplateCatalogLoader.LoadAsync(
            dataSource);
        var focusBookIds = Enumerable.Range(10_530, 6).ToArray();
        var nameplateIds = Enumerable.Range(3820, 6).ToArray();
        var historicalExclusions = focusBookIds.Concat(nameplateIds)
            .Concat(PostPetItemsV3ItemIds)
            .Append(WarehouseItemContentBaseline.StorageBoxKeyItemId)
            .Append(CaptureToolItemId)
            .Append(LegacyInstanceOpalItemContentBaseline.ItemId)
            .Concat(ClientCatalogItemContentBaseline.ItemTemplates
                .Select(static item => item.Id))
            .ToArray();
        var definitions = original.All
            .Where(item => !historicalExclusions.Contains(
                checked((int)item.Id)))
            .ToDictionary(static item => item.Id);
        foreach (var tombstone in await BuildOfficialElementalTombstonesAsync(
                     dataSource))
        {
            definitions.TryAdd(tombstone.Id, tombstone);
        }
        var predecessor = definitions.Values.OrderBy(static item => item.Id)
            .ToArray();
        var computed = ComputeV9FixtureRevision(original, predecessor);
        Check.True(
            predecessor.Length == 1758 &&
            computed == OfficialPetItemsV3Revision,
            "reviewed pets-v3 fixture reproduces the exact live predecessor");

        try
        {
            var originalIds = original.All.Select(static item => item.Id)
                .ToHashSet();
            var additions = predecessor
                .Where(item => !originalIds.Contains(item.Id))
                .ToArray();
            await CreateAndPublishV9FixtureAsync(
                dataSource,
                originalRevision,
                computed,
                OfficialPetItemsV3Source,
                predecessor.Length,
                historicalExclusions,
                additions);
            var predecessorFingerprint =
                await ReadCompleteRevisionFingerprintAsync(
                    dataSource,
                    computed);

            var upgraded = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            await using var command = dataSource.CreateCommand("""
                SELECT release.entry_count, release.source,
                       (SELECT count(*)::integer
                        FROM item_template_content_definitions definition
                        WHERE definition.revision = publication.revision
                          AND definition.id = ANY(@focusBookIds))
                FROM item_template_content_publication publication
                JOIN item_template_content_revisions release
                  ON release.revision = publication.revision
                WHERE publication.family = 'items';
                """);
            command.Parameters.AddWithValue("focusBookIds", focusBookIds);
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync() &&
                reader.GetInt32(0) ==
                    1773 + ClientCatalogItemContentBaseline.ShippedItemCount &&
                reader.GetString(1).EndsWith(
                    "pets-v5+nameplates-v1+warehouse-v1+opal-v1+" +
                    "client-catalog-v1",
                    StringComparison.Ordinal) &&
                reader.GetInt32(2) == 6,
                "exact pets-v3 upgrade publishes all six Focus books");
            Check.True(
                upgraded.Revision == ClientCatalogV1Revision &&
                await ReadCompleteRevisionFingerprintAsync(
                    dataSource,
                    computed) == predecessorFingerprint,
                "pets-v3 upgrade creates the capture-tool release and preserves its predecessor");
        }
        finally
        {
            await RestoreItemPublicationAsync(dataSource, originalRevision);
        }
    }
}
