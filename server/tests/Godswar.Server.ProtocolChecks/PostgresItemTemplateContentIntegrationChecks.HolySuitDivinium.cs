using System.Text.RegularExpressions;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    public const string HolySuitDiviniumCheckName =
        "PostgreSQL Holy Suit Divinium publication preserves the exact deployed predecessor and owned items";
    private const string HolySuitPredecessorSource =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v5+nameplates-v1+warehouse-v1+opal-v1";

    public static async Task RunHolySuitDiviniumAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString)) throw new CheckSkippedException(HolySuitDiviniumCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, "^godswar_(?:b03|b09)_[a-z0-9_]+$", RegexOptions.CultureInvariant))
                throw new CheckSkippedException(HolySuitDiviniumCheckName + " requires a disposable B03/B09 database");
        }
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var original = await PostgresItemTemplateContentBootstrapper.LoadAsync(connectionString);
        var tombstones = await BuildOfficialElementalTombstonesAsync(source);
        var definitions = original.All.Where(item => !PostPetItemsV3ItemIds.Contains(checked((int)item.Id)))
            .ToDictionary(item => item.Id);
        foreach (var tombstone in tombstones) definitions.TryAdd(tombstone.Id, tombstone);
        var predecessor = await RestoreLegacyHolySuitDefinitionsAsync(source, definitions.Values);
        var oldRevision = ComputeV9FixtureRevision(original, predecessor);
        Check.True(predecessor.Length == 1773 && oldRevision == OfficialOpalV1Revision,
            "the upgrade starts from the exact deployed Opal-v1 hash with its original seven-tier Holy Suit rows");
        var originalIds = original.All.Select(item => item.Id).ToHashSet();
        var fixtureCharacterId = 0;
        try
        {
            await CreateAndPublishV9FixtureAsync(source, original.Revision.Sha256, oldRevision,
                HolySuitPredecessorSource, predecessor.Length, PostPetItemsV3ItemIds,
                tombstones.Where(item => !originalIds.Contains(item.Id)).ToArray());
            var historicalFingerprint = await ReadCompleteRevisionFingerprintAsync(source, oldRevision);
            var characterId = await CreateHolySuitOwnedIdentityFixtureAsync(source);
            fixtureCharacterId = characterId;
            var ownedBefore = await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId);
            await SetHolySuitMutableAppearanceFromRevisionAsync(source, oldRevision);
            await using (var poison = source.CreateCommand("UPDATE item_templates SET icon='custom-icon' WHERE id=9014;"))
                Check.Equal(1, await poison.ExecuteNonQueryAsync(), "prepare an unrelated mutable material customization");
            var beforeRejected = await ReadHolySuitMutableAppearanceAsync(source);
            var rejected = false;
            try { _ = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source); }
            catch (InvalidOperationException error) when (error.Message.Contains("Mutable Holy Suit item 9014", StringComparison.Ordinal))
            { rejected = true; }
            Check.True(rejected && await ReadHolySuitCurrentPointerAsync(source) == oldRevision &&
                await ReadHolySuitMutableAppearanceAsync(source) == beforeRejected &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore,
                "an unrecognized mutable identity aborts publication atomically without replacing it or an owned item");
            await SetHolySuitMutableAppearanceFromRevisionAsync(source, oldRevision);

            var upgraded = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            var current = await PostgresItemTemplateCatalogLoader.LoadAsync(source);
            Check.True(upgraded.Revision != oldRevision && upgraded.EntryCount == 1795 &&
                current.Revision.Sha256 == upgraded.Revision && current.HolySuit.Tiers.Count == 9 &&
                current.HolySuit.Upgrades.Count == 80 && current.HolySuit.Consumables.Count == 14,
                "the deployed predecessor advances to a distinct complete eight-tier immutable publication");
            var expectedNames = new[] { "RuneSteel Ingot", "Arcanite Crystal", "Seraphite Core", "Divinium Essence" };
            var expectedIcons = new[] { "0,0", "36,0", "72,0", "108,0" };
            for (var index = 0; index < expectedNames.Length; index++)
            {
                Check.True(current.TryGet(checked((uint)(9014 + index)), out var item) &&
                    item.DisplayName == expectedNames[index] && item.Icon == expectedIcons[index] &&
                    item.Texture == HolySuitContentBaseline.WareTexture,
                    "each renamed or new ware uses its reviewed identity and dedicated icon");
            }
            Check.True(await ReadCompleteRevisionFingerprintAsync(source, oldRevision) == historicalFingerprint &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore,
                "the forward publication preserves all sealed predecessor rows and every owned equipment/material field");
            await AssertHolySuitMutableMatchesPublicationAsync(source, upgraded.Revision);
            var repeated = await PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync(source);
            Check.True(repeated.Revision == upgraded.Revision && !repeated.Created &&
                await ReadHolySuitOwnedIdentityFingerprintAsync(source, characterId) == ownedBefore,
                "retrying the forward publication is idempotent for both content and owned instances");
        }
        finally
        {
            // Later compatibility checks intentionally remove mutable FK identities.
            // Release only this proof's owned instances after their preservation checks.
            if (fixtureCharacterId != 0)
            {
                await using var cleanup = source.CreateCommand("DELETE FROM character_items WHERE user_id=@character;");
                cleanup.Parameters.AddWithValue("character", fixtureCharacterId);
                await cleanup.ExecuteNonQueryAsync();
            }
            await RestoreItemPublicationAsync(source, original.Revision.Sha256);
            await SetHolySuitMutableAppearanceFromRevisionAsync(source, original.Revision.Sha256);
        }
    }
}
