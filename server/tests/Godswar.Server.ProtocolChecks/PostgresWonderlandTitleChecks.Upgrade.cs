using System.Text.RegularExpressions;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWonderlandTitleChecks
{
    public const string UpgradeCheckName =
        "PostgreSQL Wonderland all-island title upgrade preserves existing run receipts";
    // Position 148 in the registered catalog: the exact already released five-title schema
    // that the all-island extension upgrades from. Anchored by position rather than by a
    // migration ID so that renumbering the appended migrations cannot silently retarget it.
    private const int ReleasedPredecessorLength = 148;
    private const string ReleasedPredecessorId = "20260915_147_monster_loot_policy";
    private const string WonderlandTitlesMigrationId = "20260916_148_wonderland_titles";

    public static async Task RunUpgradeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("GODSWAR_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException(UpgradeCheckName + " requires PostgreSQL");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using (var guard = source.CreateCommand("SELECT current_database();"))
        {
            var database = Convert.ToString(await guard.ExecuteScalarAsync()) ?? string.Empty;
            if (!Regex.IsMatch(database, @"^godswar_b(?:03|08|09|10|11|12)_[a-z0-9_]{1,48}$",
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                throw new CheckSkippedException(UpgradeCheckName + " requires a disposable database");
        }
        var runner = new PostgresSchemaMigrationRunner(source);
        var prefix = PostgresSchemaMigrationCatalog.All.Take(ReleasedPredecessorLength).ToArray();
        Check.True(prefix.Length == ReleasedPredecessorLength && prefix[^1].Id == ReleasedPredecessorId,
            "the forward-upgrade fixture starts at the exact already released five-title schema");
        await runner.InitializeAsync(LegacySchemaBootstrap.LoadAsync, prefix);
        var request = await CreateAsync(source);
        var store = new PostgresWonderlandTitleStore(source);
        var original = await store.SettleAsync(request);
        Check.True(original.Status == WonderlandTitleStatus.Applied && original.Award.TitleId == 5114 &&
            original.Members.Select(member => member.SelectedTitleId).SequenceEqual(new uint[] { 0, 5009 }),
            "the deployed predecessor schema grants island two while preserving both existing title selections");
        var before = await StateAsync(source, request);
        var oldHistory = await WonderlandMigrationHistoryAsync(source, WonderlandTitlesMigrationId);

        await runner.InitializeGodswarSchemaAsync();
        Check.True(await StateAsync(source, request) == before &&
            await WonderlandMigrationHistoryAsync(source, WonderlandTitlesMigrationId) == oldHistory,
            "forward migration preserves all previous title rows and the sealed predecessor checksum and timestamp");
        var replay = await new PostgresWonderlandTitleStore(source).SettleAsync(request);
        Check.True(replay.Status == WonderlandTitleStatus.Duplicate && replay.WorldInstanceId == original.WorldInstanceId &&
            replay.Award == original.Award && replay.Members.SequenceEqual(original.Members),
            "the original v1 run and request hashes still return the exact pre-upgrade receipt");
        foreach (var island in new[] { 1, 3, 5 })
        {
            var added = Copy(request, island: island, cleared: request.StartedAtUtc.AddMinutes(island));
            Check.Equal(request.RunHash, added.RunHash, "additional island awards extend the same original v1 run identity");
            var applied = await store.SettleAsync(added);
            Check.True(applied.Status == WonderlandTitleStatus.Applied &&
                applied.Award.TitleId == WonderlandTitlePolicy.Resolve(island).TitleId &&
                applied.Members.All(member => member.NewlyOwned && member.HonorPoints == 1234) &&
                applied.Members.Select(member => member.SelectedTitleId).SequenceEqual(new uint[] { 0, 5009 }),
                "the existing run can grant the new island without changing selection or wallet");
            var duplicate = await store.SettleAsync(added);
            Check.True(duplicate.Status == WonderlandTitleStatus.Duplicate && duplicate.Members.SequenceEqual(applied.Members),
                "new island receipts are equally stable under replay");
        }
        await CheckWonderlandWrongIslandTitlePairAsync(source, request);
        var after = await StateAsync(source, request);
        await runner.InitializeGodswarSchemaAsync();
        Check.True(await StateAsync(source, request) == after,
            "startup reruns never duplicate or rewrite migrated title grants");
    }

    private static async Task<string> WonderlandMigrationHistoryAsync(NpgsqlDataSource source, string migrationId)
    {
        await using var command = source.CreateCommand(
            "SELECT to_jsonb(history)::text FROM public.schema_migrations history WHERE migration_id=@id;");
        command.Parameters.AddWithValue("id", migrationId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task CheckWonderlandWrongIslandTitlePairAsync(NpgsqlDataSource source, WonderlandTitleRequest request)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO public.wonderland_title_milestones(world_instance_id,island_number,title_id,request_hash,
                cleared_at_ticks,character_ids) VALUES(@instance,8,5114,@hash,@at,@characters);
            """);
        command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
        command.Parameters.AddWithValue("hash", new string('A', 64));
        command.Parameters.AddWithValue("at", request.ClearedAtUtc.UtcTicks);
        command.Parameters.AddWithValue("characters", request.CharacterIds.ToArray());
        var refused = false;
        try { await command.ExecuteNonQueryAsync(); }
        catch (PostgresException error) when (error.SqlState == "23514") { refused = true; }
        Check.True(refused, "expanding supported islands never permits a title mapped to the wrong island");
    }
}
