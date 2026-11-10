using System.Text.RegularExpressions;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    public const string AscensionCoreCheckName =
        "PostgreSQL Ascension Core presentation preserves the deployed catalog and owned cores";

    public static async Task RunAscensionCoreAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException(AscensionCoreCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
            if (!Regex.IsMatch(Convert.ToString(await guard.ExecuteScalarAsync()) ?? "",
                "^godswar_(?:b03|b09)_[a-z0-9_]+$", RegexOptions.CultureInvariant))
                throw new CheckSkippedException(AscensionCoreCheckName + " requires a disposable B03/B09 database");
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var original = await PostgresItemTemplateContentBootstrapper.LoadAsync(connectionString);
        var originalFingerprint = await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256);
        var legacy = await BuildCanonicalHolySuitItemsAsync(source,
            HolySuitContentBaselineV3.ItemTemplates.Where(item => item.Id == 9025).ToArray());
        var reviewed = (await BuildCanonicalHolySuitItemsAsync(source,
            HolySuitContentBaseline.ItemTemplates.Where(item => item.Id == 9025).ToArray()))
            .ToDictionary(item => item.Id);
        var definitions = original.All.Where(item => !PostPetItemsV3ItemIds.Contains(checked((int)item.Id)))
            .ToDictionary(item => item.Id);
        var tombstones = await BuildOfficialElementalTombstonesAsync(source);
        foreach (var item in tombstones) definitions.TryAdd(item.Id, item);
        foreach (var item in legacy) definitions[item.Id] = item;
        var predecessor = definitions.Values.OrderBy(item => item.Id).ToArray();
        const string oldRevision = "7CD94D179AA38A51A075B6AFDA3FB3176230A0B8A09AE6360D3F11551B941440";
        Check.True(predecessor.Length == 1774 &&
            ComputeHolySuitEightTierFixtureRevision(original, predecessor) == oldRevision,
            "the Ascension Core release reproduces the exact deployed sockets-v2 predecessor");
        var characterId = 0;
        try
        {
            await CreateAndPublishV9FixtureAsync(source, original.Revision.Sha256, oldRevision,
                "items-v9+pets-v5+nameplates-v1+warehouse-v1+opal-v1+holy-v5+holy-stones-v3+sockets-v2", predecessor.Length,
                PostPetItemsV3ItemIds, tombstones.Where(item => !original.TryGet(item.Id, out _)).ToArray(),
                legacyHolySuit: false, legacyHolySuitMaterials: false, legacyHolyStoneReagents: false, legacySocketSpells: false);
            var historical = await ReadCompleteRevisionFingerprintAsync(source, oldRevision);
            characterId = await CreateHolySuitOwnedIdentityFixtureAsync(source, includeDivinium: true);
            await using (var owned = source.CreateCommand("""
                INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
                VALUES(@character,1,8,9025,1,1,1,17),(@character,1,9,9025,1,1,0,23),
                      (@character,1,10,9025,1,1,1,31),(@character,1,11,9025,1,1,0,3);
                """))
            {
                owned.Parameters.AddWithValue("character", characterId);
                Check.Equal(4, await owned.ExecuteNonQueryAsync(), "prepare owned bound and unbound existing Ascension Core stacks");
            }
            var ownedBefore = await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId);
            await SetCoreMutableAppearanceAsync(source, oldRevision);
            await using (var poison = source.CreateCommand("UPDATE item_templates SET stats=stats || '{\"unreviewed\":\"custom\"}'::jsonb WHERE id=9025;"))
                Check.Equal(1, await poison.ExecuteNonQueryAsync(), "prepare an unknown Ascension Core field");
            var rejected = false;
            var mutableBefore = await ReadCoreMutableFingerprintAsync(source);
            try { _ = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source); }
            catch (InvalidOperationException error) when (error.Message.Contains("Mutable Holy Suit item 9025", StringComparison.Ordinal))
            { rejected = true; }
            Check.True(rejected && await ReadHolySuitCurrentPointerAsync(source) == oldRevision &&
                await ReadCoreMutableFingerprintAsync(source) == mutableBefore &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore,
                "unknown metadata rolls back the mutable appearance change and publication atomically");
            await SetCoreMutableAppearanceAsync(source, oldRevision);
            var upgraded = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            var current = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
            Console.WriteLine($"[ascension-core] deployed successor revision={upgraded.Revision} entries={upgraded.EntryCount}");
            foreach (var prior in predecessor)
            {
                Check.True(current.TryGet(prior.Id, out var item), "every prior item identity remains available");
                var expected = reviewed.GetValueOrDefault(prior.Id, prior);
                Check.True(item.ClassIds.SequenceEqual(expected.ClassIds) &&
                    (item with { ClassIds = expected.ClassIds }) == expected,
                    $"item {prior.Id} changes only reviewed Ascension Core presentation");
            }
            Check.True(upgraded.Revision != oldRevision && upgraded.EntryCount == 1795 &&
                current.HolySuit.Tiers.SequenceEqual(original.HolySuit.Tiers) &&
                current.HolySuit.Upgrades.SequenceEqual(original.HolySuit.Upgrades) &&
                current.HolySuit.OperationPolicy == original.HolySuit.OperationPolicy,
                "the forward release preserves all unrelated policies");
            Check.True(current.TryGet(9025, out var core) && core.DisplayName == "Ascension Core", "the published identity exposes the new name");
            var retry = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            Check.True(retry.Revision == upgraded.Revision && !retry.Created &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore &&
                await ReadCompleteRevisionFingerprintAsync(source, oldRevision) == historical &&
                await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256) == originalFingerprint,
                "retry preserves every owned row and both sealed catalogs");
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
            await SetCoreMutableAppearanceAsync(source, original.Revision.Sha256);
        }
    }

    private static async Task SetCoreMutableAppearanceAsync(NpgsqlDataSource source, string revision)
    {
        await using var command = source.CreateCommand("""
            UPDATE item_templates mutable SET display_name=d.display_name,texture=d.texture,icon=d.icon,stats=d.stats
            FROM item_template_content_definitions d
            WHERE d.revision=@revision AND d.id=mutable.id AND mutable.id=9025;
            """);
        command.Parameters.AddWithValue("revision", revision);
        Check.Equal(1, await command.ExecuteNonQueryAsync(), "restore exact reviewed Ascension Core metadata");
    }

    private static async Task<string> ReadCoreMutableFingerprintAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT to_jsonb(i)::text FROM item_templates i WHERE id=9025;");
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
