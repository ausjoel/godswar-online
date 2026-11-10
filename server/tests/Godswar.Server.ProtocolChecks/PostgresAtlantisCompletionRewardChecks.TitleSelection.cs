using System.Text.RegularExpressions;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresAtlantisCompletionRewardChecks
{
    public const string TitleSelectionCheckName =
        "PostgreSQL title selection preserves earned ownership and fences stale sessions";

    public static async Task RunTitleSelectionAsync()
    {
        const string variable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
        var connectionString = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{TitleSelectionCheckName} ({variable} is not set)");
        }
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, @"^godswar_b(?:03|08|09|10|11|12)_[a-z0-9_]{1,48}$",
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            {
                throw new CheckSkippedException($"{TitleSelectionCheckName} requires a disposable database");
            }
        }
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var gameStore = new PostgresGameStore(connectionString);
        await gameStore.EnsureSeedDataAsync();
        await CheckEarnedTitleSelectionAsync(source, gameStore);
        await CheckTitleSelectionRejectionsAsync(source);
        await CheckTitleSelectionRewardRaceAsync(source);
        await CheckAdministrativeTitleEntitlementsAsync(source, gameStore);
    }

    private static async Task CheckEarnedTitleSelectionAsync(NpgsqlDataSource source, PostgresGameStore gameStore)
    {
        var fixture = await CreateFixtureAsync(source, 1);
        Check.True((await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request)).Succeeded,
            "title fixture earns a real Atlantis title and completion receipt");
        var request = await CreateTitleSelectionRequestAsync(source, fixture, 5014);
        var before = (await ReadCharactersAsync(source, fixture)).Single();
        var counts = await ReadRewardCountsAsync(source, fixture);
        var unchanged = await new PostgresCharacterTitleSelectionStore(source).SelectAsync(request with { TitleId = 0 });
        Check.True(unchanged.Status == CharacterTitleSelectionStatus.Unchanged && unchanged.SelectedTitleId == 0 &&
            unchanged.HonorPoints == before.Honor && unchanged.RewardRevision == before.Revision &&
            unchanged.OwnedTitleIds.SequenceEqual(new uint[] { 5014 }),
            "a newly earned Atlantis title remains unequipped and an explicit unchanged choice returns the current wallet");

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            new PostgresCharacterTitleSelectionStore(source).SelectAsync(request)));
        Check.True(concurrent.Count(result => result.Status == CharacterTitleSelectionStatus.Applied) == 1 &&
            concurrent.Count(result => result.Status == CharacterTitleSelectionStatus.Unchanged) == 5 &&
            concurrent.All(result => result.SelectedTitleId == 5014 && result.HonorPoints == before.Honor &&
                result.RewardRevision == before.Revision + 1 && result.OwnedTitleIds.SequenceEqual(new uint[] { 5014 })),
            "concurrent identical equip requests advance the shared revision once and preserve the complete award");
        var restarted = new PostgresCharacterTitleSelectionStore(source);
        var hidden = await restarted.SelectAsync(request with { TitleId = 0 });
        Check.True(hidden.Status == CharacterTitleSelectionStatus.Applied && hidden.SelectedTitleId == 0 &&
            hidden.RewardRevision == before.Revision + 2 && hidden.HonorPoints == before.Honor &&
            await ReadRewardCountsAsync(source, fixture) == counts,
            "a restarted store can unequip without consuming title ownership or granting another reward");
        await AssertSelectedTitleSnapshotAsync(source, gameStore, fixture, hidden);

        var medusa = await GrantSelectionMedusaTitleAsync(source, fixture);
        Check.True(medusa.Award.AwardedTitleId == 5011, "the second selection fixture earns a real Medusa title");
        var selectedAtlantis = await restarted.SelectAsync(request);
        var selectedMedusa = await restarted.SelectAsync(request with { TitleId = 5011 });
        Check.True(selectedAtlantis.Status == CharacterTitleSelectionStatus.Applied &&
            selectedMedusa.Status == CharacterTitleSelectionStatus.Applied &&
            selectedMedusa.RewardRevision == selectedAtlantis.RewardRevision + 1 &&
            selectedMedusa.HonorPoints == hidden.HonorPoints + medusa.Award.HardPoints &&
            selectedAtlantis.OwnedTitleIds.SequenceEqual(new uint[] { 5011, 5014 }) &&
            selectedMedusa.OwnedTitleIds.SequenceEqual(selectedAtlantis.OwnedTitleIds),
            "both earned title tables authorize manual selection and both ownerships survive switching titles");
        await AssertSelectedTitleSnapshotAsync(source, gameStore, fixture, selectedMedusa);
        var hiddenBoth = await restarted.SelectAsync(request with { TitleId = 0 });
        await AssertSelectedTitleSnapshotAsync(source, gameStore, fixture, hiddenBoth);
        Check.True(hiddenBoth.Succeeded && hiddenBoth.HonorPoints == selectedMedusa.HonorPoints &&
            hiddenBoth.OwnedTitleIds.SequenceEqual(new uint[] { 5011, 5014 }) &&
            await ReadRewardCountsAsync(source, fixture) == counts,
            "hiding an equipped Medusa title preserves both title entitlements and all HardPoints");
    }

    private static async Task<CharacterTitleSelectionRequest> CreateTitleSelectionRequestAsync(
        NpgsqlDataSource source, CompletionFixture fixture, uint titleId)
    {
        var member = fixture.Request.AdmittedMembers.Single();
        var fence = await PlayerOwnershipTestFences.InstallAsync(source, member.AccountId, member.CharacterId);
        return new(new(member.AccountId, member.CharacterId), RealmId.Tempest, fence, titleId);
    }

    private static async Task<MedusaCompletionRewardReceipt> GrantSelectionMedusaTitleAsync(
        NpgsqlDataSource source, CompletionFixture fixture)
    {
        var request = new MedusaCompletionRewardRequest(WorldInstanceId.New(), RealmId.Tempest,
            MedusaEncounterDifficulty.Enhanced, completed: true, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9),
            MedusaIslandPolicy.VictoryScore, fixture.Request.AdmittedCharacterIds);
        var receipt = await new PostgresMedusaCompletionRewardStore(source).SettleAsync(request);
        Check.True(receipt.Status == MedusaCompletionRewardStatus.Applied,
            "the title-selection fixture persists a real Medusa completion and ownership provenance");
        return receipt;
    }

    private static async Task AssertSelectedTitleSnapshotAsync(NpgsqlDataSource source, PostgresGameStore gameStore,
        CompletionFixture fixture, CharacterTitleSelectionReceipt expected)
    {
        await using var reader = new PostgresCharacterSnapshotReader(source, gameStore.ItemContent.Templates);
        var snapshot = await reader.ReadAsync(fixture.Request.AdmittedMembers.Single().AccountId);
        Check.True(snapshot.Character is { } character &&
            character.Appearance.SelectedTitleId == expected.SelectedTitleId &&
            character.Appearance.OwnedTitleIds.SequenceEqual(expected.OwnedTitleIds) &&
            character.Wallet.MedusaHonorPoints == expected.HonorPoints &&
            character.Wallet.MedusaRewardRevision == expected.RewardRevision,
            "reconnect reads the manually selected title, complete ownership, and the same wallet revision");
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot);
        Check.True(hydrated is not null && hydrated.Character.SelectedTitleId == expected.SelectedTitleId &&
            hydrated.Character.OwnedTitleIds.SequenceEqual(expected.OwnedTitleIds),
            "gameplay hydration preserves a manual title selection or explicit unequip");
    }
}
