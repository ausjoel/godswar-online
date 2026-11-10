using System.Text.RegularExpressions;
using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    public const string HolySuitMaterialsCheckName =
        "PostgreSQL Holy Suit material names preserve the released eight-tier content and owned stacks";
    private const string OfficialHolySuitV4Revision =
        "0310903B9C52C0F2A8EFB4EF3D71F22ED387E6CF692865C53263B40793B9A9C3";
    private const string OfficialHolySuitV4Source =
        "items-v9+pets-v5+nameplates-v1+warehouse-v1+opal-v1+holy-v4";

    public static async Task RunHolySuitMaterialsAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException(HolySuitMaterialsCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, "^godswar_(?:b03|b09)_[a-z0-9_]+$", RegexOptions.CultureInvariant))
                throw new CheckSkippedException(HolySuitMaterialsCheckName + " requires a disposable B03/B09 database");
        }
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var original = await PostgresItemTemplateContentBootstrapper.LoadAsync(connectionString);
        var originalFingerprint = await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256);
        var originalIds = original.All.Select(item => item.Id).ToHashSet();
        var tombstones = await BuildOfficialElementalTombstonesAsync(source);
        var deployedDefinitions = original.All.Where(item => !PostPetItemsV3ItemIds.Contains(checked((int)item.Id)))
            .ToDictionary(item => item.Id);
        foreach (var tombstone in tombstones) deployedDefinitions.TryAdd(tombstone.Id, tombstone);
        var releasedItems = await BuildCanonicalHolySuitItemsAsync(source, HolySuitContentBaselineV2.ItemTemplates);
        var releasedIds = releasedItems.Select(item => item.Id).ToHashSet();
        var predecessor = await RestoreLegacyHolyStoneReagentsAsync(source,
            deployedDefinitions.Values.Where(item => !releasedIds.Contains(item.Id)).Concat(releasedItems));
        var oldRevision = ComputeHolySuitEightTierFixtureRevision(original, predecessor);
        Check.True(predecessor.Length == 1774 && oldRevision == OfficialHolySuitV4Revision,
            "the rename starts from the exact deployed holy-v4 revision with all fourteen old material identities");
        var currentItems = await BuildCanonicalHolySuitItemsAsync(source, HolySuitContentBaseline.ItemTemplates);
        var currentReagents = (await BuildCanonicalHolySuitItemsAsync(source,
            HolyStoneMaterialItemContentV3.ItemTemplates.Concat(SocketSpellItemContentV2.ItemTemplates)
                .Concat(HolySuitContentBaseline.ItemTemplates).ToArray()))
            .ToDictionary(item => item.Id);
        var currentSacks = await BuildCanonicalHolySuitItemsAsync(source,
            WonderlandSackItemContentBaseline.ItemTemplates
                .Concat(ExperiencePillItemContentBaseline.ItemTemplates)
                .Concat(PetSkillBookItemContentBaseline.ItemTemplates.Where(item => item.Id >= 16400)).ToArray());
        var expectedRevision = ComputeHolySuitEightTierFixtureRevision(original,
            predecessor.Where(item => !releasedIds.Contains(item.Id)).Concat(currentItems)
                .Select(item => currentReagents.GetValueOrDefault(item.Id, item))
                .Concat(currentSacks).OrderBy(item => item.Id).ToArray());
        var characterId = 0;
        try
        {
            await CreateAndPublishV9FixtureAsync(source, original.Revision.Sha256, oldRevision,
                OfficialHolySuitV4Source, predecessor.Length, PostPetItemsV3ItemIds,
                tombstones.Where(item => !originalIds.Contains(item.Id)).ToArray(), legacyHolySuit: false);
            var oldCatalog = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
            Check.Equal(oldRevision, oldCatalog.Revision.Sha256, "the predecessor is sealed and independently loadable");
            var historicalBefore = await ReadCompleteRevisionFingerprintAsync(source, oldRevision);
            characterId = await CreateHolySuitOwnedIdentityFixtureAsync(source, includeDivinium: true);
            var ownedBefore = await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId);
            await SetHolySuitMutableAppearanceFromRevisionAsync(source, oldRevision, includeDivinium: true);
            await using (var poison = source.CreateCommand("UPDATE item_templates SET stats=stats || '{\"unreviewed\":\"custom\"}'::jsonb WHERE id=9017;"))
                Check.Equal(1, await poison.ExecuteNonQueryAsync(), "prepare an unreviewed Divinium identity field");
            var mutableBefore = await ReadHolySuitMutableAppearanceAsync(source);
            var rejected = false;
            try { _ = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source); }
            catch (InvalidOperationException error) when (error.Message.Contains("Mutable Holy Suit item 9017", StringComparison.Ordinal))
            { rejected = true; }
            Check.True(rejected && await ReadHolySuitCurrentPointerAsync(source) == oldRevision &&
                await ReadHolySuitMutableAppearanceAsync(source) == mutableBefore &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore &&
                await ReadCompleteRevisionFingerprintAsync(source, oldRevision) == historicalBefore,
                "an unrecognized fourth material aborts atomically without partial renames, publication or owned-item changes");
            await SetHolySuitMutableAppearanceFromRevisionAsync(source, oldRevision, includeDivinium: true);

            var upgraded = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            var current = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
            Check.True(upgraded.Revision != oldRevision && upgraded.Revision == expectedRevision &&
                upgraded.EntryCount == 1795 && current.HolySuit.Tiers.SequenceEqual(oldCatalog.HolySuit.Tiers) &&
                current.HolySuit.Upgrades.SequenceEqual(oldCatalog.HolySuit.Upgrades) &&
                current.HolySuit.Consumables.SequenceEqual(oldCatalog.HolySuit.Consumables) &&
                current.HolySuit.OperationPolicy == oldCatalog.HolySuit.OperationPolicy,
                "the forward revision changes material names and preserves every tier, recipe, stack cap and operation policy");
            var expectedNames = new[] { "RuneSteel Ingot", "Arcanite Crystal", "Seraphite Core", "Divinium Essence" };
            foreach (var old in predecessor)
            {
                Check.True(current.TryGet(old.Id, out var item), "every released item identity remains available");
                var prior = currentReagents.GetValueOrDefault(old.Id, old);
                var expectedName = old.Id is >= 9014 and <= 9017 ? expectedNames[old.Id - 9014] : old.DisplayName;
                if (old.Id == 9054) expectedName = "Platinum Evasion Signet";
                if (old.Id == 9025) expectedName = "Ascension Core";
                Check.True(item.DisplayName == expectedName && item.ClassIds.SequenceEqual(prior.ClassIds) &&
                    (item with { DisplayName = prior.DisplayName, ClassIds = prior.ClassIds }) == prior,
                    $"published item {old.Id} preserves all fields except approved material names and reagent artwork");
            }
            await AssertHolySuitMutableMatchesPublicationAsync(source, upgraded.Revision);
            await using (var release = source.CreateCommand("SELECT source FROM item_template_content_revisions WHERE revision=@revision;"))
            {
                release.Parameters.AddWithValue("revision", upgraded.Revision);
                Check.Equal(OfficialHolySuitV4Source[..^1].Replace("pets-v5", "pets-v7", StringComparison.Ordinal) + "5+holy-stones-v3+sockets-v2+ascension-v1+wonderland-v1+exp-pill-v1", Convert.ToString(await release.ExecuteScalarAsync())!,
                    "the new immutable release records material and reagent provenance");
            }
            var repeated = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            Check.True(repeated.Revision == upgraded.Revision && !repeated.Created &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore &&
                await ReadCompleteRevisionFingerprintAsync(source, oldRevision) == historicalBefore &&
                await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256) == originalFingerprint,
                "publication and retry preserve both sealed catalogs, including fresh-bootstrap extras, and every owned stack/equipment field");
        }
        finally
        {
            if (characterId != 0)
            {
                await using var cleanup = source.CreateCommand("DELETE FROM character_items WHERE user_id=@character;");
                cleanup.Parameters.AddWithValue("character", characterId);
                await cleanup.ExecuteNonQueryAsync();
            }
            await RestoreItemPublicationAsync(source, original.Revision.Sha256);
            await SetHolySuitMutableAppearanceFromRevisionAsync(source, original.Revision.Sha256, includeDivinium: true);
        }
    }

    private static string ComputeHolySuitEightTierFixtureRevision(PinnedItemTemplateCatalog catalog,
        IReadOnlyList<ItemTemplateDefinition> definitions) =>
        ItemTemplateContentRevisionHasher.ComputeV6(definitions,
            catalog.Attributes, catalog.EquipmentRanks, catalog.HolySuitEffects,
            catalog.Materials.ForgingMaterials, catalog.Materials.GearEnhancementMaterials,
            catalog.Materials.AttributeDusts, catalog.Materials.GearMentorRecipes,
            HolySuitContentBaselineV2.Tiers, HolySuitContentBaselineV2.Upgrades,
            HolySuitContentBaselineV2.Consumables, HolySuitContentBaselineV2.OperationPolicy);
}
