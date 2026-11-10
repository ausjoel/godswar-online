using System.Text.RegularExpressions;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    public const string ExperiencePillCheckName =
        "PostgreSQL EXP Pill publication appends one native reward and preserves the deployed Wonderland catalog";
    private const string WonderlandPrePillRevision =
        "38161E6B26E30F671B90ABAE6A0F3BB1879777D5FD6F78712A9BA2F76596E404";
    private const string WonderlandPrePillSource =
        "items-v9+pets-v5+nameplates-v1+warehouse-v1+opal-v1+holy-v5+holy-stones-v3+sockets-v2+ascension-v1+wonderland-v1";

    public static async Task RunExperiencePillAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException(ExperiencePillCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
            if (!Regex.IsMatch(Convert.ToString(await guard.ExecuteScalarAsync()) ?? "",
                "^godswar_(?:b03|b09)_[a-z0-9_]+$", RegexOptions.CultureInvariant))
                throw new CheckSkippedException(ExperiencePillCheckName + " requires a disposable B03/B09 database");
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var original = await PostgresItemTemplateContentBootstrapper.LoadAsync(connectionString);
        var originalFingerprint = await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256);
        // Keep all twelve sacks in their deployed predecessor; only the later
        // pill and older fresh-bootstrap extras are absent from this lineage.
        var exclusions = PostPetItemsV3ItemIds.Where(id => id is < 4450 or > 4461).ToArray();
        var definitions = original.All.Where(item => !exclusions.Contains(checked((int)item.Id)))
            .ToDictionary(item => item.Id);
        var tombstones = await BuildOfficialElementalTombstonesAsync(source);
        foreach (var item in tombstones) definitions.TryAdd(item.Id, item);
        var predecessor = definitions.Values.OrderBy(item => item.Id).ToArray();
        Check.True(predecessor.Length == 1786 &&
            ComputeHolySuitEightTierFixtureRevision(original, predecessor) == WonderlandPrePillRevision,
            "the pill upgrade reproduces the exact deployed 1786-item Wonderland release");
        var pill = (await BuildCanonicalHolySuitItemsAsync(source, ExperiencePillItemContentBaseline.ItemTemplates)).Single();
        var books = await BuildCanonicalHolySuitItemsAsync(source,
            PetSkillBookItemContentBaseline.ItemTemplates.Where(item => item.Id >= 16400).ToArray());
        var bloodfang = await BuildCanonicalHolySuitItemsAsync(source,
            PetItemContentBaseline.ItemTemplates.Where(item => item.Id is 10194 or 11096).ToArray());
        var expectedRevision = ComputeHolySuitEightTierFixtureRevision(original,
            predecessor.Append(pill).Concat(books).Concat(bloodfang).OrderBy(item => item.Id).ToArray());
        var characterId = 0;
        try
        {
            await CreateAndPublishV9FixtureAsync(source, original.Revision.Sha256, WonderlandPrePillRevision,
                WonderlandPrePillSource, predecessor.Length, exclusions,
                tombstones.Where(item => !original.TryGet(item.Id, out _)).ToArray(),
                legacyHolySuit: false, legacyHolySuitMaterials: false, legacyHolyStoneReagents: false,
                legacySocketSpells: false, legacyAscensionCore: false);
            var historical = await ReadCompleteRevisionFingerprintAsync(source, WonderlandPrePillRevision);
            characterId = await CreateHolySuitOwnedIdentityFixtureAsync(source, includeDivinium: true);
            var ownedBefore = await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId);
            await using (var poison = source.CreateCommand(
                "UPDATE item_templates SET stats=stats || '{\"Skill\":\"5150\"}'::jsonb WHERE id=4174;"))
                Check.Equal(1, await poison.ExecuteNonQueryAsync(), "prepare an unreviewed EXP Pill activation skill");
            var mutableBefore = await ReadMutableExperiencePillAsync(source);
            var rejected = false;
            try { _ = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source); }
            catch (InvalidOperationException error) when (error.Message.Contains("Mutable EXP Pill 4174", StringComparison.Ordinal))
            { rejected = true; }
            Check.True(rejected && await ReadHolySuitCurrentPointerAsync(source) == WonderlandPrePillRevision &&
                await ReadMutableExperiencePillAsync(source) == mutableBefore &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore &&
                await ReadCompleteRevisionFingerprintAsync(source, WonderlandPrePillRevision) == historical,
                "conflicting pill metadata rolls back publication without modifying inventory or sealed content");
            await RestoreMutableExperiencePillAsync(source, original.Revision.Sha256);
            await using (var remove = source.CreateCommand("""
                DELETE FROM item_templates t WHERE id=4174
                  AND NOT EXISTS(SELECT 1 FROM character_items i WHERE i.prop_id=t.id);
                """))
                await remove.ExecuteNonQueryAsync();
            var upgraded = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            var current = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
            Check.True(upgraded.Revision == expectedRevision && upgraded.EntryCount == 1795 &&
                current.All.Select(item => item.Id).Except(predecessor.Select(item => item.Id))
                    .SequenceEqual(new[] { 4174u }.Concat(books.Select(item => item.Id))
                        .Concat(bloodfang.Select(item => item.Id)).Order()),
                "the forward release adds EXP Pill, Vampiric books and Bloodfang inventory identities");
            foreach (var prior in predecessor)
                Check.True(current.TryGet(prior.Id, out var item) && item.ClassIds.SequenceEqual(prior.ClassIds) &&
                    (item with { ClassIds = prior.ClassIds }) == prior,
                    $"published item {prior.Id} remains exactly unchanged");
            Check.True(current.TryGet(4174, out var publishedPill) && publishedPill.ClassIds.SequenceEqual(pill.ClassIds) &&
                (publishedPill with { ClassIds = pill.ClassIds }) == pill &&
                current.HolySuit.Tiers.SequenceEqual(original.HolySuit.Tiers) &&
                current.HolySuit.Upgrades.SequenceEqual(original.HolySuit.Upgrades) &&
                current.HolySuit.Consumables.SequenceEqual(original.HolySuit.Consumables) &&
                current.HolySuit.OperationPolicy == original.HolySuit.OperationPolicy &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore,
                "the pill matches native metadata while every policy and existing owned row is preserved");
            await using (var match = source.CreateCommand("""
                SELECT count(*) FROM item_templates m JOIN official_item_template_content d USING(id)
                WHERE m.id=4174 AND
                ROW(m.kind,m.name_key,m.display_name,m.equipment_slot,m.class_ids,m.min_level,m.max_level,
                    m.hand,m.skill_flag,m.texture,m.icon,m.stats) IS NOT DISTINCT FROM
                ROW(d.kind,d.name_key,d.display_name,d.equipment_slot,d.class_ids,d.min_level,d.max_level,
                    d.hand,d.skill_flag,d.texture,d.icon,d.stats);
                """))
                Check.Equal(1L, (long)(await match.ExecuteScalarAsync())!, "the native pill FK template is available to rewards");
            await using (var owned = source.CreateCommand("""
                INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
                VALUES(@character,1,8,4174,1,1,1,17),(@character,1,9,4174,1,1,0,23);
                """))
            {
                owned.Parameters.AddWithValue("character", characterId);
                Check.Equal(2, await owned.ExecuteNonQueryAsync(), "prepare owned bound and unbound pill stacks");
            }
            var ownedPills = await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId);
            var retry = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            Check.True(retry.Revision == upgraded.Revision && !retry.Created &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedPills &&
                await ReadCompleteRevisionFingerprintAsync(source, WonderlandPrePillRevision) == historical &&
                await ReadCompleteRevisionFingerprintAsync(source, original.Revision.Sha256) == originalFingerprint,
                "reuse preserves both owned pill stacks and every immutable historical row");
            await using var provenance = source.CreateCommand("SELECT source FROM item_template_content_revisions WHERE revision=@revision;");
            provenance.Parameters.AddWithValue("revision", upgraded.Revision);
            Check.Equal(WonderlandPrePillSource.Replace("pets-v5", "pets-v7", StringComparison.Ordinal) + "+exp-pill-v1", (string)(await provenance.ExecuteScalarAsync())!,
                "the new release records explicit EXP Pill provenance");
            Console.WriteLine($"[experience-pill] revision={upgraded.Revision} entries={upgraded.EntryCount}");
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
            await RestoreMutableExperiencePillAsync(source, original.Revision.Sha256);
        }
    }

    private static async Task<string?> ReadMutableExperiencePillAsync(NpgsqlDataSource source)
    {
        await using var read = source.CreateCommand("SELECT to_jsonb(t)::text FROM item_templates t WHERE id=4174;");
        return (string?)await read.ExecuteScalarAsync();
    }

    private static async Task RestoreMutableExperiencePillAsync(NpgsqlDataSource source, string revision)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO item_templates(id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                hand,skill_flag,texture,icon,stats)
            SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,max_level,
                hand,skill_flag,texture,icon,stats FROM item_template_content_definitions
            WHERE revision=@revision AND id=4174
            ON CONFLICT(id) DO UPDATE SET stats=EXCLUDED.stats;
            """);
        command.Parameters.AddWithValue("revision", revision);
        await command.ExecuteNonQueryAsync();
    }
}
