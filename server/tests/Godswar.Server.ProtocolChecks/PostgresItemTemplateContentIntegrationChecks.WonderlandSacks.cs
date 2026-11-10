using System.Text.RegularExpressions;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    public const string WonderlandSacksCheckName =
        "PostgreSQL Wonderland original sacks and EXP Pill preserve the deployed catalog";

    public static async Task RunWonderlandSacksAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException(WonderlandSacksCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
            if (!Regex.IsMatch(Convert.ToString(await guard.ExecuteScalarAsync()) ?? "",
                "^godswar_(?:b03|b09)_[a-z0-9_]+$", RegexOptions.CultureInvariant))
                throw new CheckSkippedException(WonderlandSacksCheckName + " requires a disposable B03/B09 database");
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var original = await PostgresItemTemplateContentBootstrapper.LoadAsync(connectionString);
        var originalFingerprint = await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256);
        var definitions = original.All.Where(item => !PostPetItemsV3ItemIds.Contains(checked((int)item.Id)))
            .ToDictionary(item => item.Id);
        var tombstones = await BuildOfficialElementalTombstonesAsync(source);
        foreach (var item in tombstones) definitions.TryAdd(item.Id, item);
        var predecessor = definitions.Values.OrderBy(item => item.Id).ToArray();
        const string oldRevision = "7FF15E627A18307A8DD9B596E27BF3EA268E0DE1151DFA8C8841BB25D0584BE5";
        Check.True(predecessor.Length == 1774 &&
            ComputeHolySuitEightTierFixtureRevision(original, predecessor) == oldRevision,
            "the sack release reconstructs the exact deployed Ascension Core predecessor");
        var characterId = 0;
        try
        {
            await CreateAndPublishV9FixtureAsync(source, original.Revision.Sha256, oldRevision,
                "items-v9+pets-v5+nameplates-v1+warehouse-v1+opal-v1+holy-v5+holy-stones-v3+sockets-v2+ascension-v1",
                predecessor.Length, PostPetItemsV3ItemIds,
                tombstones.Where(item => !original.TryGet(item.Id, out _)).ToArray(),
                legacyHolySuit: false, legacyHolySuitMaterials: false, legacyHolyStoneReagents: false,
                legacySocketSpells: false, legacyAscensionCore: false);
            var historical = await ReadCompleteRevisionFingerprintAsync(source, oldRevision);
            characterId = await CreateHolySuitOwnedIdentityFixtureAsync(source, includeDivinium: true);
            var ownedBefore = await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId);
            await using (var poison = source.CreateCommand(
                "UPDATE item_templates SET stats=stats || '{\"Overlap\":\"100\"}'::jsonb WHERE id=4450;"))
                Check.Equal(1, await poison.ExecuteNonQueryAsync(), "prepare a conflicting sack stack cap");
            var rejected = false;
            try { _ = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source); }
            catch (InvalidOperationException error) when (error.Message.Contains("Mutable Wonderland sack 4450", StringComparison.Ordinal))
            { rejected = true; }
            Check.True(rejected && await ReadHolySuitCurrentPointerAsync(source) == oldRevision &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore,
                "conflicting native sack metadata rolls back publication and preserves owned inventory");
            await RestoreMutableSacksAsync(source, original.Revision.Sha256);
            await using (var remove = source.CreateCommand("""
                DELETE FROM item_templates t WHERE id BETWEEN 4450 AND 4461
                  AND NOT EXISTS(SELECT 1 FROM character_items i WHERE i.prop_id=t.id);
                """))
                await remove.ExecuteNonQueryAsync();
            var upgraded = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            var current = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
            Check.Equal(1795, upgraded.EntryCount, "the deployed catalog gains sacks, EXP Pill and reviewed Vampiric books");
            Check.True(current.All.Select(item => item.Id).Except(predecessor.Select(item => item.Id))
                .Order().SequenceEqual(Enumerable.Range(4450, 12).Concat(Enumerable.Range(16400, 6))
                    .Select(id => (uint)id).Append(4174u).Order()),
                "only the reviewed sacks, EXP Pill and Vampiric identities are added");
            foreach (var prior in predecessor)
            {
                Check.True(current.TryGet(prior.Id, out var item) && item.ClassIds.SequenceEqual(prior.ClassIds) &&
                    (item with { ClassIds = prior.ClassIds }) == prior,
                    $"existing item {prior.Id} remains exactly unchanged");
            }
            Check.True(current.HolySuit.Tiers.SequenceEqual(original.HolySuit.Tiers) &&
                current.HolySuit.Upgrades.SequenceEqual(original.HolySuit.Upgrades) &&
                current.HolySuit.Consumables.SequenceEqual(original.HolySuit.Consumables) &&
                current.HolySuit.OperationPolicy == original.HolySuit.OperationPolicy,
                "sack publication preserves the full Holy Suit economy");
            await using (var match = source.CreateCommand("""
                SELECT count(*) FROM item_templates m JOIN official_item_template_content d USING(id)
                WHERE m.id BETWEEN 4450 AND 4461 AND
                ROW(m.kind,m.name_key,m.display_name,m.equipment_slot,m.class_ids,m.min_level,m.max_level,
                    m.hand,m.skill_flag,m.texture,m.icon,m.stats) IS NOT DISTINCT FROM
                ROW(d.kind,d.name_key,d.display_name,d.equipment_slot,d.class_ids,d.min_level,d.max_level,
                    d.hand,d.skill_flag,d.texture,d.icon,d.stats);
                """))
            {
                Check.Equal(12L, (long)(await match.ExecuteScalarAsync())!, "all twelve sack templates are visible to inventory persistence");
            }
            var retry = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            Check.True(retry.Revision == upgraded.Revision && !retry.Created &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore &&
                await ReadCompleteRevisionFingerprintAsync(source, oldRevision) == historical &&
                await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256) == originalFingerprint,
                "retry preserves owned rows and both immutable releases");
            Console.WriteLine($"[wonderland-sacks] revision={upgraded.Revision} entries={upgraded.EntryCount}");
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
            await RestoreMutableSacksAsync(source, original.Revision.Sha256);
        }
    }

    private static async Task RestoreMutableSacksAsync(NpgsqlDataSource source, string revision)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO item_templates(id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                hand,skill_flag,texture,icon,stats)
            SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                hand,skill_flag,texture,icon,stats FROM item_template_content_definitions
            WHERE revision=@revision AND id BETWEEN 4450 AND 4461
            ON CONFLICT(id) DO UPDATE SET stats=EXCLUDED.stats;
            """);
        command.Parameters.AddWithValue("revision", revision);
        await command.ExecuteNonQueryAsync();
    }
}
