using System.Text.RegularExpressions;
using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    public const string VampiricCloneCheckName =
        "PostgreSQL Vampiric publication rehearsal preserves cloned inventory and historical items";
    private const string PreVampiricItemRevision =
        "A45EA650680C8EA4D5D2FCAA831E97EEEF652BAB55351D860BFB95330FA97EB1";
    private const string VampiricItemSource =
        "items-v9+pets-v7+nameplates-v1+warehouse-v1+opal-v1+holy-v5+holy-stones-v3+sockets-v2+ascension-v1+wonderland-v1+exp-pill-v1";

    // Run once against a disposable clone of the deployed predecessor. Unlike
    // historical fixture checks, this performs only normal content publication:
    // no invented character/items, trigger bypasses or publication rewinds.
    public static async Task RunVampiricCloneAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException(VampiricCloneCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            if (!Regex.IsMatch(Convert.ToString(await guard.ExecuteScalarAsync()) ?? "",
                    "^(?:godswar_(?:b03|b09|vampiric)_[a-z0-9_]+|godswar_b12_vampiric_20260914)$", RegexOptions.CultureInvariant))
                throw new CheckSkippedException(VampiricCloneCheckName + " requires a disposable clone");
        }

        var prior = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
        Check.True(prior.Revision.Sha256 == PreVampiricItemRevision && prior.Revision.EntryCount == 1787,
            "the clone starts at the exact deployed 1787-item EXP Pill predecessor");
        var historical = new Dictionary<string, string>(StringComparer.Ordinal);
        var revisions = new List<string>();
        await using (var command = source.CreateCommand("SELECT revision FROM item_template_content_revisions ORDER BY revision;"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) revisions.Add(reader.GetString(0));
        foreach (var revision in revisions)
            historical.Add(revision, await ReadCompleteRevisionFingerprintAsync(source, revision));
        var ownedBefore = await ReadVampiricCloneInventoryAsync(source);
        var mutableBefore = await ReadVampiricCloneMutableItemsAsync(source);
        var books = await BuildCanonicalHolySuitItemsAsync(source,
            PetSkillBookItemContentBaseline.ItemTemplates.Where(item => item.Id is >= 16400 and <= 16405).ToArray());
        var bloodfang = await BuildCanonicalHolySuitItemsAsync(source,
            PetItemContentBaseline.ItemTemplates.Where(item => item.Id is 10194 or 11096).ToArray());
        Check.True(books.Length == 6 && books.All(item => !prior.TryGet(item.Id, out _)),
            "all six authored book IDs are new to the deployed catalog");
        var operationPolicy = prior.HolySuit.OperationPolicy ?? throw new InvalidDataException(
            "The deployed predecessor is missing its Holy Suit operation policy.");
        var expected = ItemTemplateContentRevisionHasher.ComputeV6(
            prior.All.Concat(books).Concat(bloodfang).OrderBy(item => item.Id).ToArray(),
            prior.Attributes, prior.EquipmentRanks, prior.HolySuitEffects,
            prior.Materials.ForgingMaterials, prior.Materials.GearEnhancementMaterials,
            prior.Materials.AttributeDusts, prior.Materials.GearMentorRecipes,
            prior.HolySuit.Tiers, prior.HolySuit.Upgrades, prior.HolySuit.Consumables,
            operationPolicy);

        var current = await PostgresItemTemplateContentBootstrapper.LoadAsync(source);
        var learned = await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(connectionString);
        Check.True(current.Revision.Sha256 == expected && current.Revision.EntryCount == 1795 &&
            current.All.Select(item => item.Id).Except(prior.All.Select(item => item.Id))
                .SequenceEqual(Enumerable.Range(16400, 6).Select(id => checked((uint)id))
                    .Concat(new[] { 10194u, 11096u }).Order()),
            "normal publication adds six books and the Bloodfang egg/jade with existing policies preserved");
        foreach (var old in prior.All)
            Check.True(current.TryGet(old.Id, out var item) && item.ClassIds.SequenceEqual(old.ClassIds) &&
                (item with { ClassIds = old.ClassIds }) == old,
                $"pre-existing item {old.Id} remains identical");
        foreach (var book in books)
        {
            Check.True(current.TryGet(book.Id, out var item) && item.ClassIds.SequenceEqual(book.ClassIds) &&
                (item with { ClassIds = book.ClassIds }) == book &&
                PetSkillBookActivationPolicy.TryResolve(current, learned, book.Id, out var activation) &&
                activation.FamilyType == 428 && activation.RuntimeSkillId == checked((int)book.Id - 10000) &&
                activation.Priority == checked((short)(book.Id - 16399)),
                $"book {book.Id} matches native metadata and the published learned family");
        }
        await AssertPetItemPublicationAsync(source, current);
        var repeat = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
        Check.True(repeat.Revision == expected && !repeat.Created,
            "repeated startup reuses the completed book publication");
        Check.True(await ReadVampiricCloneInventoryAsync(source) == ownedBefore &&
            await ReadVampiricCloneMutableItemsAsync(source) == mutableBefore,
            "all owned bag, equipment and warehouse rows and pre-existing FK templates remain unchanged");
        foreach (var (revision, fingerprint) in historical)
            Check.Equal(fingerprint, await ReadCompleteRevisionFingerprintAsync(source, revision),
                $"historical item revision {revision} remains sealed and identical");
        await using var provenance = source.CreateCommand(
            "SELECT source FROM item_template_content_revisions WHERE revision=@revision;");
        provenance.Parameters.AddWithValue("revision", expected);
        Check.Equal(VampiricItemSource, (string)(await provenance.ExecuteScalarAsync())!,
            "the release records the reviewed pets-v7 provenance");
        Console.WriteLine($"[vampiric-items] revision={expected} entries={current.Revision.EntryCount} " +
            $"historicalRevisions={historical.Count} inventoryUnchanged=true learned={learned.Revision.Sha256}");
    }

    private static async Task<string> ReadVampiricCloneInventoryAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT md5(COALESCE(jsonb_agg(to_jsonb(item) ORDER BY item.id)::text, '[]'))
            FROM public.character_items item;
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadVampiricCloneMutableItemsAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT md5(COALESCE(jsonb_agg(to_jsonb(item) ORDER BY item.id)::text, '[]'))
            FROM public.item_templates item WHERE item.id NOT BETWEEN 16400 AND 16405
              AND item.id NOT IN (10194, 11096);
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
