using System.Text.RegularExpressions;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresAtlantisCompletionRewardChecks
{
    public const string CheckName = "PostgreSQL Atlantis completion rewards, original admission, and exactly-once titles";

    public static async Task RunAsync()
    {
        const string variable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
        var connectionString = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({variable} is not set)");
        }
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, @"^godswar_b(?:03|08|09|10|11|12)_[a-z0-9_]{1,48}$",
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            {
                throw new CheckSkippedException($"{CheckName} requires a disposable database; received '{database}'");
            }
        }
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();

        await CheckAwardAndRestartAsync(source, store, 1);
        await CheckAwardAndRestartAsync(source, store, 5);
        await CheckDepartedMembersExcludedAsync(source);
        await CheckConflictingEvidenceAsync(source);
        await CheckAdmissionRejectionsAsync(source);
        await CheckUnavailableAndOverflowAsync(source);
        await CheckMidPartyRollbackAsync(source);
    }

    private static async Task CheckAwardAndRestartAsync(NpgsqlDataSource source, PostgresGameStore gameStore,
        int partySize)
    {
        var fixture = await CreateFixtureAsync(source, partySize);
        if (partySize > 1)
        {
            for (var index = 0; index < partySize; index++)
            {
                await UpdateFixtureAsync(source, fixture,
                    "UPDATE public.character_base SET selected_title_id=5009 WHERE id=@character;", index);
            }
        }
        var before = await ReadCharactersAsync(source, fixture);
        Check.True(before.All(character => character.SelectedTitle == (partySize == 1 ? 0 : 5009)),
            "completion fixtures cover both no selected title and a previously selected different title");
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request)));
        Check.Equal(1, results.Count(result => result.Status == AtlantisCompletionRewardStatus.Applied),
            "concurrent callers produce exactly one authoritative completion award");
        Check.Equal(4, results.Count(result => result.Status == AtlantisCompletionRewardStatus.Duplicate),
            "all other concurrent callers recover the original completion receipt");
        var applied = results.Single(result => result.Status == AtlantisCompletionRewardStatus.Applied);
        Check.True(applied.Succeeded && applied.WorldInstanceId == fixture.Request.WorldInstanceId &&
            applied.Award.HardPoints == 2800 && applied.Award.TitleId == (partySize == 1 ? 5014u : 5013u) &&
            applied.Members.Count == partySize,
            "the original admitted party size selects the native solo or team completion title");
        var after = await ReadCharactersAsync(source, fixture);
        for (var index = 0; index < before.Length; index++)
        {
            var receipt = applied.Members.Single(member => member.CharacterId == before[index].CharacterId);
            Check.True(receipt.AccountId == before[index].AccountId && receipt.Camp == before[index].Camp &&
                receipt.HonorBefore == before[index].Honor && receipt.HonorAfter == before[index].Honor + 2800 &&
                receipt.RewardRevision == 8 && receipt.AwardedTitleId == applied.Award.TitleId &&
                after[index].Honor == receipt.HonorAfter && after[index].Revision == 8 &&
                after[index].SelectedTitle == before[index].SelectedTitle,
                "each eligible finisher receives2800 HardPoints and title ownership while preserving an empty or existing title selection");
        }
        Check.True(await ReadRewardCountsAsync(source, fixture) == new RewardRowCounts(1, partySize, partySize),
            "one completion header owns exactly the paid member and title rows");

        var restarted = new PostgresAtlantisCompletionRewardStore(source);
        var replay = await restarted.SettleAsync(CopyRequest(fixture.Request,
            admitted: fixture.Request.AdmittedMembers.Reverse().ToArray(),
            finishers: fixture.Request.FrozenMembers.Reverse().ToArray()));
        Check.True(replay.Status == AtlantisCompletionRewardStatus.Duplicate &&
            replay.Members.OrderBy(member => member.CharacterId).SequenceEqual(
                applied.Members.OrderBy(member => member.CharacterId)) &&
            (await ReadCharactersAsync(source, fixture)).SequenceEqual(after),
            "a restarted store and reordered equivalent roster recover the same receipt without a second grant");

        await using var reader = new PostgresCharacterSnapshotReader(source, gameStore.ItemContent.Templates);
        foreach (var member in fixture.Request.FrozenMembers)
        {
            var snapshot = await reader.ReadAsync(member.AccountId);
            var previous = before.Single(row => row.CharacterId == member.CharacterId);
            Check.True(snapshot.Character is { } character &&
                character.Appearance.SelectedTitleId == previous.SelectedTitle &&
                character.Appearance.OwnedTitleIds.Contains(applied.Award.TitleId) &&
                character.Wallet.MedusaHonorPoints == before.Single(row => row.CharacterId == member.CharacterId).Honor + 2800,
                "reconnect restores Atlantis title ownership and HardPoints without selecting the new title");
            var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot);
            Check.True(hydrated is not null && hydrated.Character.SelectedTitleId == previous.SelectedTitle &&
                hydrated.Character.OwnedTitleIds.Contains(applied.Award.TitleId),
                "hydrated gameplay character retains earned title ownership and the prior title selection");
        }
    }

    private static async Task CheckDepartedMembersExcludedAsync(NpgsqlDataSource source)
    {
        var fixture = await CreateFixtureAsync(source, 5, finisherCount: 1);
        var before = await ReadCharactersAsync(source, fixture);
        var result = await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request);
        Check.True(result.Status == AtlantisCompletionRewardStatus.Applied && result.Members.Count == 1 &&
            result.Award.TitleId == 5013,
            "a five-person admission with one finisher pays only that finisher and retains the team title");
        var after = await ReadCharactersAsync(source, fixture);
        Check.True(after[0].Honor == before[0].Honor + 2800 && after[0].SelectedTitle == before[0].SelectedTitle &&
            after.Skip(1).SequenceEqual(before.Skip(1)) &&
            await ReadRewardCountsAsync(source, fixture) == new RewardRowCounts(1, 1, 1),
            "four departed original members receive neither HardPoints nor title ownership");
        var changedFinishers = await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(
            CopyRequest(fixture.Request, finishers: fixture.Request.AdmittedMembers.ToArray()));
        Check.True(changedFinishers.Status == AtlantisCompletionRewardStatus.RequestConflict &&
            (await ReadCharactersAsync(source, fixture)).SequenceEqual(after),
            "a replay cannot add departed members back into the paid completion roster");
    }
}
