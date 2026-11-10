using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertBloodfangPetItemsUpgradeAsync(
        NpgsqlDataSource source,
        PinnedItemTemplateCatalog original)
    {
        const string predecessorRevision =
            "A9987EF33AC288A99A86CC18043C3E91005A816B3003CB34AE7B1B5CAC20931E";
        const string predecessorSource =
            "items-v9+pets-v6+nameplates-v1+warehouse-v1+opal-v1+holy-v5+holy-stones-v3+sockets-v2+ascension-v1+wonderland-v1+exp-pill-v1";
        var exclusions = PostPetItemsV3ItemIds
            .Where(id => id != 4174 && id is not (>= 16400 and <= 16405) &&
                id is not (>= 4450 and <= 4461)).ToArray();
        var tombstones = await BuildOfficialElementalTombstonesAsync(source);
        var definitions = original.All
            .Where(item => !exclusions.Contains(checked((int)item.Id)))
            .ToDictionary(item => item.Id);
        foreach (var item in tombstones) definitions.TryAdd(item.Id, item);
        var predecessor = definitions.Values.OrderBy(item => item.Id).ToArray();
        Check.True(predecessor.Length == 1793 &&
            ComputeHolySuitEightTierFixtureRevision(original, predecessor) == predecessorRevision,
            "Bloodfang fixture reproduces the exact reviewed pets-v6 publication");
        var additions = await BuildCanonicalHolySuitItemsAsync(source,
            PetItemContentBaseline.ItemTemplates.Where(item => item.Id is 10194 or 11096).ToArray());
        var expected = ComputeHolySuitEightTierFixtureRevision(original,
            predecessor.Concat(additions).OrderBy(item => item.Id).ToArray());
        var originalFingerprint = await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256);
        var inventoryBefore = await ReadVampiricCloneInventoryAsync(source);
        try
        {
            await CreateAndPublishV9FixtureAsync(source, original.Revision.Sha256,
                predecessorRevision, predecessorSource, predecessor.Length, exclusions,
                tombstones.Where(item => !original.TryGet(item.Id, out _)).ToArray(),
                legacyHolySuit: false, legacyHolySuitMaterials: false,
                legacyHolyStoneReagents: false, legacySocketSpells: false, legacyAscensionCore: false);
            var predecessorFingerprint = await ReadCompleteRevisionFingerprintAsync(source, predecessorRevision);
            await using (var poison = source.CreateCommand(
                "UPDATE item_templates SET display_name='unreviewed Bloodfang' WHERE id=10194;"))
                Check.Equal(1, await poison.ExecuteNonQueryAsync(), "prepare conflicting mutable Bloodfang identity");
            var rejected = false;
            try { _ = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source); }
            catch (InvalidOperationException error) when (
                error.Message.Contains("Pet item 10194 conflicts", StringComparison.Ordinal))
            { rejected = true; }
            Check.True(rejected && await ReadHolySuitCurrentPointerAsync(source) == predecessorRevision,
                "conflicting Bloodfang metadata rolls back publication");
            await RestoreBloodfangMutableItemsAsync(source, original.Revision.Sha256);
            await using (var remove = source.CreateCommand("""
                DELETE FROM item_templates t WHERE id IN (10194,11096)
                  AND NOT EXISTS(SELECT 1 FROM character_items i WHERE i.prop_id=t.id);
                """))
                Check.Equal(2, await remove.ExecuteNonQueryAsync(), "prepare both missing Bloodfang FK identities");
            var result = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            var current = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
            Check.True(result.Revision == expected && result.EntryCount == 1795 &&
                current.All.Select(item => item.Id).Except(predecessor.Select(item => item.Id))
                    .SequenceEqual(new[] { 10194u, 11096u }),
                "pets-v6 automatically publishes exactly the Bloodfang egg and jade");
            foreach (var old in predecessor)
                Check.True(current.TryGet(old.Id, out var item) && item.ClassIds.SequenceEqual(old.ClassIds) &&
                    (item with { ClassIds = old.ClassIds }) == old,
                    $"Bloodfang publication preserves prior item {old.Id}");
            await AssertPetItemPublicationAsync(source, current);
            Check.True(await ReadVampiricCloneInventoryAsync(source) == inventoryBefore &&
                await ReadCompleteRevisionFingerprintAsync(source, predecessorRevision) == predecessorFingerprint &&
                await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256) == originalFingerprint,
                "Bloodfang publication preserves owned inventory and sealed predecessor revisions");
        }
        finally
        {
            await RestoreItemPublicationAsync(source, original.Revision.Sha256);
            await RestoreBloodfangMutableItemsAsync(source, original.Revision.Sha256);
        }
    }

    private static async Task RestoreBloodfangMutableItemsAsync(NpgsqlDataSource source, string revision)
    {
        await using var restore = source.CreateCommand("""
            INSERT INTO item_templates (
                id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                hand,skill_flag,texture,icon,stats)
            SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                   hand,skill_flag,texture,icon,stats
            FROM item_template_content_definitions WHERE revision=@revision AND id IN (10194,11096)
            ON CONFLICT(id) DO UPDATE SET display_name=EXCLUDED.display_name;
            """);
        restore.Parameters.AddWithValue("revision", revision);
        await restore.ExecuteNonQueryAsync();
    }
}
