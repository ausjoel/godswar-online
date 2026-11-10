using System.Text.RegularExpressions;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    public const string HolyStoneReagentsCheckName =
        "PostgreSQL Holy Stone reagent release preserves the deployed catalog and owned signets";

    public static async Task RunHolyStoneReagentsAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException(HolyStoneReagentsCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
            if (!Regex.IsMatch(Convert.ToString(await guard.ExecuteScalarAsync()) ?? "",
                "^godswar_(?:b03|b09)_[a-z0-9_]+$", RegexOptions.CultureInvariant))
                throw new CheckSkippedException(HolyStoneReagentsCheckName + " requires a disposable B03/B09 database");
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var original = await PostgresItemTemplateContentBootstrapper.LoadAsync(connectionString);
        var originalFingerprint = await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256);
        var definitions = original.All.Where(item => !PostPetItemsV3ItemIds.Contains(checked((int)item.Id)))
            .ToDictionary(item => item.Id);
        var tombstones = await BuildOfficialElementalTombstonesAsync(source);
        foreach (var item in tombstones) definitions.TryAdd(item.Id, item);
        var predecessor = await RestoreLegacyHolyStoneReagentsAsync(source, definitions.Values);
        const string oldRevision = "2948180014012F2DD38CDC89FD0172071F49BC765BA55E0AD13410571484E791";
        Check.True(predecessor.Length == 1774 &&
            ComputeHolySuitEightTierFixtureRevision(original, predecessor) == oldRevision,
            "the reagent release reproduces the exact deployed holy-v5 predecessor");
        var characterId = 0;
        try
        {
            await CreateAndPublishV9FixtureAsync(source, original.Revision.Sha256, oldRevision,
                "items-v9+pets-v5+nameplates-v1+warehouse-v1+opal-v1+holy-v5", predecessor.Length,
                PostPetItemsV3ItemIds, tombstones.Where(item => !original.TryGet(item.Id, out _)).ToArray(),
                legacyHolySuit: false, legacyHolySuitMaterials: false);
            var historical = await ReadCompleteRevisionFingerprintAsync(source, oldRevision);
            characterId = await CreateHolySuitOwnedIdentityFixtureAsync(source, includeDivinium: true);
            await using (var owned = source.CreateCommand("""
                INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
                VALUES(@character,1,8,9054,1,1,1,17),(@character,1,9,9053,1,1,0,23),
                      (@character,1,10,9042,1,1,1,31),(@character,1,11,9055,1,1,1,3);
                """))
            {
                owned.Parameters.AddWithValue("character", characterId);
                Check.Equal(4, await owned.ExecuteNonQueryAsync(), "prepare owned signets, legacy tombstone and Eclipse stacks");
            }
            var ownedBefore = await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId);
            await SetReagentMutableAppearanceAsync(source, oldRevision);
            await using (var poison = source.CreateCommand("UPDATE item_templates SET stats=stats || '{\"unreviewed\":\"custom\"}'::jsonb WHERE id=9054;"))
                Check.Equal(1, await poison.ExecuteNonQueryAsync(), "prepare an unreviewed Platinum predecessor");
            var rejected = false;
            var mutableBefore = await ReadReagentMutableFingerprintAsync(source);
            try { _ = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source); }
            catch (InvalidOperationException error) when (error.Message.Contains("Mutable Holy Stone item 9054", StringComparison.Ordinal))
            { rejected = true; }
            Check.True(rejected && await ReadHolySuitCurrentPointerAsync(source) == oldRevision &&
                await ReadReagentMutableFingerprintAsync(source) == mutableBefore &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore,
                "unreviewed metadata aborts all eight mutable updates and publication atomically");
            await SetReagentMutableAppearanceAsync(source, oldRevision);
            var upgraded = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            var current = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
            var reviewed = (await BuildCanonicalHolySuitItemsAsync(source,
                HolyStoneMaterialItemContentV3.ItemTemplates.Concat(SocketSpellItemContentV2.ItemTemplates)
                    .Concat(HolySuitContentBaseline.ItemTemplates).ToArray()))
                .ToDictionary(item => item.Id);
            foreach (var prior in predecessor)
            {
                Check.True(current.TryGet(prior.Id, out var item), "the forward release retains every identity");
                var expected = reviewed.GetValueOrDefault(prior.Id, prior);
                Check.True(item.ClassIds.SequenceEqual(expected.ClassIds) &&
                    (item with { ClassIds = expected.ClassIds }) == expected,
                    $"item {prior.Id} matches only the reviewed reagent changes");
            }
            Check.True(upgraded.Revision != oldRevision && upgraded.EntryCount == 1795 &&
                current.HolySuit.Tiers.SequenceEqual(original.HolySuit.Tiers) &&
                current.HolySuit.Upgrades.SequenceEqual(original.HolySuit.Upgrades) &&
                current.HolySuit.OperationPolicy == original.HolySuit.OperationPolicy,
                "new reagent publication preserves all unrelated tier and upgrade content");
            await AssertHolyStoneMaterialPublicationAsync(source, current);
            var retry = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            Check.True(retry.Revision == upgraded.Revision && !retry.Created &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore &&
                await ReadCompleteRevisionFingerprintAsync(source, oldRevision) == historical &&
                await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256) == originalFingerprint,
                "retry preserves every owned row and both sealed catalogs, including the original Gold identity");
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
            await SetReagentMutableAppearanceAsync(source, original.Revision.Sha256);
        }
    }

    private static async Task SetReagentMutableAppearanceAsync(NpgsqlDataSource source, string revision)
    {
        await using var command = source.CreateCommand("""
            UPDATE item_templates mutable SET display_name=d.display_name,texture=d.texture,icon=d.icon,stats=d.stats
            FROM item_template_content_definitions d
            WHERE d.revision=@revision AND d.id=mutable.id AND mutable.id=ANY(@ids);
            """);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("ids", HolyStoneMaterialItemContentV3.ReagentIds);
        Check.Equal(8, await command.ExecuteNonQueryAsync(), "restore reviewed mutable reagent metadata");
    }

    private static async Task<string> ReadReagentMutableFingerprintAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT jsonb_agg(to_jsonb(i) ORDER BY id)::text FROM item_templates i WHERE id=ANY(@ids);");
        command.Parameters.AddWithValue("ids", HolyStoneMaterialItemContentV3.ReagentIds);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
