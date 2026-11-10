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

internal static partial class PostgresWonderlandTitleChecks
{
    public const string CheckName = "PostgreSQL Wonderland milestone titles are durable, selectable, and never auto-equipped";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("GODSWAR_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new CheckSkippedException(CheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, @"^godswar_b(?:03|08|09|10|11|12)_[a-z0-9_]{1,48}$",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                throw new CheckSkippedException(CheckName + " requires a disposable database");
        }
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var gameStore = new PostgresGameStore(connectionString);
        await gameStore.EnsureSeedDataAsync();
        await CheckMilestonesAndSelectionAsync(source, gameStore);
        await CheckRefusalsAsync(source);
        await CheckRollbackAsync(source);
        await CheckConcurrentRunsAsync(source);
    }

    private static async Task CheckMilestonesAndSelectionAsync(NpgsqlDataSource source, PostgresGameStore gameStore)
    {
        var request = await CreateAsync(source, 3, eligibleCount: 2);
        // Clear evidence predates a disconnect/transport and a new checkpoint
        // owner. Entitlement is retained; manual selection still needs the new owner.
        await MutateAsync(source, request, """
            UPDATE public.character_base SET checkpoint_owner_id=NULL,checkpoint_owner_generation=checkpoint_owner_generation+1
            WHERE id=@character;
            """);
        foreach (var island in Enumerable.Range(1, 8))
        {
            var current = Copy(request, island: island, cleared: request.StartedAtUtc.AddMinutes(island));
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                new PostgresWonderlandTitleStore(source).SettleAsync(current)));
            Check.True(results.Count(result => result.Status == WonderlandTitleStatus.Applied) == 1 &&
                results.Count(result => result.Status == WonderlandTitleStatus.Duplicate) == 3 &&
                results.All(result => result.Members.Count == 2 && result.Members.All(member => member.HonorPoints == 1234)),
                "each actual cleared island durably grants only its frozen eligible members, once under concurrent retries");
        }
        await using var snapshots = new PostgresCharacterSnapshotReader(source, gameStore.ItemContent.Templates);
        var expectedTitles = Enumerable.Range(1, 8).Select(island => WonderlandTitlePolicy.Resolve(island).TitleId)
            .Order().ToArray();
        for (var index = 0; index < request.AdmittedMembers.Count; index++)
        {
            var member = request.AdmittedMembers[index];
            var snapshot = await snapshots.ReadAsync(member.AccountId);
            var character = snapshot.Character!;
            Check.True(character.Appearance.SelectedTitleId == (index % 2 == 0 ? 0 : 5009) &&
                character.Wallet.MedusaHonorPoints == 1234 &&
                character.Wallet.MedusaRewardRevision == (index < 2 ? 15 : 7) &&
                character.Appearance.OwnedTitleIds.SequenceEqual(index < 2 ? expectedTitles : []),
                "snapshot restores earned Wonderland titles without changing prior selection, wallet, or excluded members");
            var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot);
            Check.True(hydrated is not null && hydrated.Character.OwnedTitleIds.SequenceEqual(character.Appearance.OwnedTitleIds),
                "reconnect hydration retains all milestone ownerships");
        }
        var first = request.FrozenMembers[0];
        var currentOwner = new PlayerOwnershipFence(Guid.NewGuid(), first.Ownership.Generation + 2);
        await using (var replace = source.CreateCommand("""
            UPDATE public.character_base SET checkpoint_owner_id=@owner,checkpoint_owner_generation=@generation WHERE id=@character;
            """))
        {
            replace.Parameters.AddWithValue("owner", currentOwner.OwnerId);
            replace.Parameters.AddWithValue("generation", currentOwner.Generation);
            replace.Parameters.AddWithValue("character", first.CharacterId);
            await replace.ExecuteNonQueryAsync();
        }
        var selector = new PostgresCharacterTitleSelectionStore(source);
        var selection = new CharacterTitleSelectionRequest(new(first.AccountId, first.CharacterId), RealmId.Tempest, currentOwner, 5118);
        Check.True((await selector.SelectAsync(selection with { Ownership = first.Ownership })).Status ==
            CharacterTitleSelectionStatus.OwnershipLost, "old island-clear ownership never authorizes a later manual selection");
        var selected = await selector.SelectAsync(selection);
        Check.True(selected.Succeeded && selected.SelectedTitleId == 5118 && selected.HonorPoints == 1234 &&
            selected.RewardRevision == 16 && selected.OwnedTitleIds.SequenceEqual(expectedTitles),
            "the current owner can manually equip a Wonderland title using the complete ownership union");
        foreach (var island in new[] { 1, 3, 5 })
        {
            var next = selection with { TitleId = WonderlandTitlePolicy.Resolve(island).TitleId };
            var equipped = await selector.SelectAsync(next);
            Check.True(equipped.Succeeded && equipped.SelectedTitleId == next.TitleId &&
                equipped.RewardRevision == selected.RewardRevision + 1 && equipped.HonorPoints == 1234 &&
                equipped.OwnedTitleIds.SequenceEqual(expectedTitles),
                "each newly added island title is selectable with one shared revision increment and no wallet change");
            var repeated = await selector.SelectAsync(next);
            Check.True(repeated.Status == CharacterTitleSelectionStatus.Unchanged &&
                repeated.RewardRevision == equipped.RewardRevision,
                "repeated manual selection of a newly added title does not increment its revision twice");
            selected = equipped;
        }
        var beforeReplay = await StateAsync(source, request);
        Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(request)).Status == WonderlandTitleStatus.Duplicate &&
            await StateAsync(source, request) == beforeReplay,
            "a restarted store replays the original milestone without overwriting newer title selection or revision");
    }

    private static async Task CheckConcurrentRunsAsync(NpgsqlDataSource source)
    {
        var first = await CreateAsync(source, 1);
        var second = Copy(first, instance: WorldInstanceId.New(), reservation: Guid.NewGuid(),
            started: first.StartedAtUtc.AddDays(1), cleared: first.ClearedAtUtc.AddDays(1));
        await InsertAdmissionsAsync(source, second);
        var results = await Task.WhenAll(new PostgresWonderlandTitleStore(source).SettleAsync(first),
            new PostgresWonderlandTitleStore(source).SettleAsync(second));
        Check.True(results.All(result => result.Status == WonderlandTitleStatus.Applied) &&
            results.SelectMany(result => result.Members).Count(member => member.NewlyOwned) == 1 &&
            results.All(result => result.Members.Single().RewardRevision == 8),
            "separate valid runs grant the same permanent title once and increment ownership revision once");
        await MutateAsync(source, second,
            "UPDATE public.character_base SET medusa_reward_revision=9223372036854775807 WHERE id=@character;");
        var third = Copy(second, instance: WorldInstanceId.New(), reservation: Guid.NewGuid(),
            started: second.StartedAtUtc.AddDays(1), cleared: second.ClearedAtUtc.AddDays(1));
        await InsertAdmissionsAsync(source, third);
        var repeated = await new PostgresWonderlandTitleStore(source).SettleAsync(third);
        Check.True(repeated.Succeeded && !repeated.Members.Single().NewlyOwned &&
            repeated.Members.Single().RewardRevision == long.MaxValue,
            "an already owned title remains settleable without revision overflow or duplicate ownership");
    }
}
