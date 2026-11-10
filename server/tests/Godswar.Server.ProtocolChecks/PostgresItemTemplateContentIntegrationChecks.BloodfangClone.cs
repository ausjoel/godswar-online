using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    public const string BloodfangCloneCheckName =
        "PostgreSQL Bloodfang cloned item publication preserves predecessor and inventory";
    private const string BloodfangClonePredecessorRevision =
        "A9987EF33AC288A99A86CC18043C3E91005A816B3003CB34AE7B1B5CAC20931E";

    // Inspect the already-published B12 rehearsal. Only normal publisher reuse
    // runs here: no schema bootstrap, gameplay-store seeding or fixture rewind.
    public static async Task RunBloodfangCloneUpgradeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException(BloodfangCloneCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
            if (!string.Equals(Convert.ToString(await guard.ExecuteScalarAsync()),
                    "godswar_b12_bloodfang_20260914", StringComparison.Ordinal))
                throw new CheckSkippedException(BloodfangCloneCheckName + " requires the disposable Bloodfang B12 clone");

        await using (var predecessor = source.CreateCommand("""
            SELECT entry_count, manifest_version, source, sealed_at IS NOT NULL
            FROM item_template_content_revisions WHERE revision=@revision;
            """))
        {
            predecessor.Parameters.AddWithValue("revision", BloodfangClonePredecessorRevision);
            await using var reader = await predecessor.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync() && reader.GetInt32(0) == 1793 &&
                reader.GetInt16(1) == 9 && reader.GetBoolean(3) &&
                reader.GetString(2) == VampiricItemSource.Replace("pets-v7", "pets-v6", StringComparison.Ordinal),
                "the clone retains the exact sealed 1793-item pets-v6 predecessor");
        }
        var current = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
        Check.Equal(1795, current.Revision.EntryCount, "Bloodfang publication has exactly 1795 items");
        var oldDefinitions = current.All.Where(item => item.Id is not (10194 or 11096)).ToArray();
        var operationPolicy = current.HolySuit.OperationPolicy ?? throw new InvalidDataException(
            "Bloodfang publication is missing its Holy Suit operation policy.");
        var oldHash = ItemTemplateContentRevisionHasher.ComputeV6(
            oldDefinitions, current.Attributes, current.EquipmentRanks, current.HolySuitEffects,
            current.Materials.ForgingMaterials, current.Materials.GearEnhancementMaterials,
            current.Materials.AttributeDusts, current.Materials.GearMentorRecipes,
            current.HolySuit.Tiers, current.HolySuit.Upgrades, current.HolySuit.Consumables, operationPolicy);
        Check.Equal(BloodfangClonePredecessorRevision, oldHash,
            "removing only the Bloodfang egg and jade reproduces the exact prior manifest hash");
        var additions = await BuildCanonicalHolySuitItemsAsync(source,
            PetItemContentBaseline.ItemTemplates.Where(item => item.Id is 10194 or 11096).ToArray());
        Check.Equal(2, additions.Length, "both reviewed Bloodfang item seeds are available");
        foreach (var expected in additions)
            Check.True(current.TryGet(expected.Id, out var item) && item.ClassIds.SequenceEqual(expected.ClassIds) &&
                (item with { ClassIds = expected.ClassIds }) == expected,
                $"Bloodfang item {expected.Id} exactly matches reviewed inventory metadata");
        Check.True(await ReadBloodfangCloneRevisionRowsAsync(source, "item_template_content_definitions",
                BloodfangClonePredecessorRevision) ==
            await ReadBloodfangCloneRevisionRowsAsync(source, "item_template_content_definitions",
                current.Revision.Sha256, excludeBloodfang: true),
            "all 1793 prior item definitions remain exactly identical, including reserved item 11095");
        foreach (var table in BloodfangClonePolicyTables)
            Check.True(await ReadBloodfangCloneRevisionRowsAsync(source, table, BloodfangClonePredecessorRevision) ==
                await ReadBloodfangCloneRevisionRowsAsync(source, table, current.Revision.Sha256),
                $"Bloodfang publication preserves every row in {table}");

        var historical = new Dictionary<string, string>(StringComparer.Ordinal);
        var revisions = new List<string>();
        await using (var command = source.CreateCommand("SELECT revision FROM item_template_content_revisions ORDER BY revision;"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) revisions.Add(reader.GetString(0));
        foreach (var revision in revisions)
            historical.Add(revision, await ReadCompleteRevisionFingerprintAsync(source, revision));
        var inventoryBefore = await ReadVampiricCloneInventoryAsync(source);
        var mutableBefore = await ReadBloodfangCloneMutableItemsAsync(source);
        await AssertPetItemPublicationAsync(source, current);
        var repeat = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
        Check.True(repeat.Revision == current.Revision.Sha256 && repeat.EntryCount == 1795 && !repeat.Created,
            "the completed Bloodfang publication is reused without another revision");
        Check.True(await ReadVampiricCloneInventoryAsync(source) == inventoryBefore &&
            await ReadBloodfangCloneMutableItemsAsync(source) == mutableBefore,
            "Bloodfang publisher reuse preserves every owned inventory row and mutable FK template");
        foreach (var (revision, fingerprint) in historical)
            Check.Equal(fingerprint, await ReadCompleteRevisionFingerprintAsync(source, revision),
                $"Bloodfang publisher reuse preserves sealed item revision {revision}");
        Console.WriteLine($"[bloodfang-clone-items] predecessor={oldHash} revision={repeat.Revision} " +
            $"entries={repeat.EntryCount} additions=10194,11096 historicalRevisions={historical.Count} inventoryUnchanged=true");
    }

    private static readonly string[] BloodfangClonePolicyTables =
    [
        "item_attribute_content_definitions", "equipment_rank_content_definitions",
        "holy_suit_effect_content_definitions", "item_material_content_definitions",
        "holy_suit_tier_content_definitions", "holy_suit_upgrade_content_definitions",
        "holy_suit_consumable_content_definitions", "holy_suit_operation_policy_content_definitions"
    ];

    private static async Task<string> ReadBloodfangCloneRevisionRowsAsync(
        NpgsqlDataSource source, string table, string revision, bool excludeBloodfang = false)
    {
        // Table names are exclusively the constants above or the item table.
        var exclusion = excludeBloodfang ? " AND definition.id NOT IN (10194,11096)" : string.Empty;
        await using var command = source.CreateCommand($"""
            SELECT COALESCE(jsonb_agg(to_jsonb(definition)-'revision'
                ORDER BY (to_jsonb(definition)-'revision')::text)::text, '[]')
            FROM {table} definition WHERE definition.revision=@revision{exclusion};
            """);
        command.Parameters.AddWithValue("revision", revision);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadBloodfangCloneMutableItemsAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT md5(COALESCE(jsonb_agg(to_jsonb(item) ORDER BY item.id)::text, '[]'))
            FROM public.item_templates item;
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
