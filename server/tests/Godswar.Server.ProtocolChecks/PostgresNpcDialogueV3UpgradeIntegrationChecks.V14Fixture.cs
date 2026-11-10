using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private const string V14FixturePublisher =
        "protocol-check-canonical-v14-fixture";

    private static async Task SeedAndPublishCanonicalV14FixtureAsync(
        NpgsqlDataSource dataSource)
    {
        await SeedAndPublishCanonicalV13FixtureAsync(dataSource);
        var texts = NpcDialogueBaselineV14.ApplyTextOverrides(
            await ReadOfficialV8TextsAsync(dataSource));
        var routes = NpcDialogueBaselineV14.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);
        Check.True(
            texts.Length == NpcDialogueBaselineV14.ExpectedTextCount &&
            routes.Length == NpcDialogueBaselineV14.ExpectedRouteCount &&
            revision.EntryCount ==
                NpcDialogueBaselineV14.ExpectedHashedEntryCount &&
            revision.Sha256 == NpcDialogueBaselineV14.ExpectedRevision,
            "fixture data reproduces the exact canonical V14 revision");

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(1193657936, 1448298802);",
                         connection,
                         transaction))
        {
            await locked.ExecuteNonQueryAsync();
        }

        await InsertV14ReleaseAsync(connection, transaction);
        await InsertV14TextsAsync(connection, transaction, texts);
        await CopyV13GeometryToV14Async(connection, transaction);
        await PublishV14FixtureAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task InsertV14ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_revisions (
                revision, spawn_revision, text_count, profile_count,
                route_count, menu_entry_count, source)
            VALUES (
                @revision, @spawnRevision, @textCount, @profileCount,
                @routeCount, @menuEntryCount, @source);
            """,
            connection,
            transaction);
        AddV14Revision(command);
        command.Parameters.AddWithValue(
            "spawnRevision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV14.ExpectedSpawnRevision);
        command.Parameters.AddWithValue(
            "textCount", NpcDialogueBaselineV14.ExpectedTextCount);
        command.Parameters.AddWithValue(
            "profileCount", NpcDialogueBaselineV14.ExpectedProfileCount);
        command.Parameters.AddWithValue(
            "routeCount", NpcDialogueBaselineV14.ExpectedRouteCount);
        command.Parameters.AddWithValue(
            "menuEntryCount", NpcDialogueBaselineV14.ExpectedMenuEntryCount);
        command.Parameters.AddWithValue(
            "source", NpgsqlDbType.Varchar, NpcDialogueBaselineV14.Source);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical V14 release is inserted");
    }

    private static async Task InsertV14TextsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<NpcTextDefinition> texts)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_texts (
                revision, npc_key, scene_key, display_name, description)
            VALUES (
                @revision, @npcKey, @sceneKey, @displayName, @description);
            """,
            connection,
            transaction);
        foreach (var text in texts)
        {
            command.Parameters.Clear();
            AddV14Revision(command);
            command.Parameters.AddWithValue(
                "npcKey", NpgsqlDbType.Varchar, text.NpcKey);
            command.Parameters.AddWithValue(
                "sceneKey", NpgsqlDbType.Varchar, text.SceneKey);
            command.Parameters.AddWithValue(
                "displayName", NpgsqlDbType.Varchar, text.DisplayName);
            command.Parameters.AddWithValue(
                "description", NpgsqlDbType.Text, text.Description);
            Check.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                $"canonical V14 text {text.NpcKey}");
        }
    }

    private static async Task CopyV13GeometryToV14Async(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_profiles (
                revision, profile_key, dialog_index, behavior,
                initial_request_sub_id)
            SELECT @v14Revision, profile_key, dialog_index, behavior,
                   initial_request_sub_id
            FROM npc_dialogue_profiles
            WHERE revision = @v13Revision;

            INSERT INTO npc_dialogue_profile_entries (
                revision, profile_key, menu_order, sub_id)
            SELECT @v14Revision, profile_key, menu_order, sub_id
            FROM npc_dialogue_profile_entries
            WHERE revision = @v13Revision;

            INSERT INTO npc_dialogue_bindings (
                revision, npc_key, client_script_key, profile_key, route_order)
            SELECT @v14Revision, npc_key, client_script_key,
                   profile_key, route_order
            FROM npc_dialogue_bindings
            WHERE revision = @v13Revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "v13Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV13.ExpectedRevision);
        command.Parameters.AddWithValue(
            "v14Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV14.ExpectedRevision);
        Check.Equal(
            NpcDialogueBaselineV14.ExpectedProfileCount +
            NpcDialogueBaselineV14.ExpectedMenuEntryCount +
            NpcDialogueBaselineV14.ExpectedRouteCount,
            await command.ExecuteNonQueryAsync(),
            "canonical V14 geometry is copied from immutable V13");
    }

    private static async Task PublishV14FixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_publication (
                family, revision, published_at, publisher)
            VALUES ('npc-dialogues', @revision, now(), @publisher)
            ON CONFLICT (family) DO UPDATE
            SET revision = EXCLUDED.revision,
                published_at = EXCLUDED.published_at,
                publisher = EXCLUDED.publisher;
            """,
            connection,
            transaction);
        AddV14Revision(command);
        command.Parameters.AddWithValue(
            "publisher", NpgsqlDbType.Varchar, V14FixturePublisher);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical V14 fixture publication pointer");
    }

    private static void AddV14Revision(NpgsqlCommand command) =>
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV14.ExpectedRevision);

    private static async Task<string> ReadCanonicalV14SnapshotAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT jsonb_build_object(
                'release', (SELECT to_jsonb(release)
                    FROM npc_dialogue_revisions release
                    WHERE release.revision = @revision),
                'texts', (SELECT jsonb_agg(to_jsonb(text_row)
                    ORDER BY text_row.npc_key)
                    FROM npc_dialogue_texts text_row
                    WHERE text_row.revision = @revision),
                'profiles', (SELECT jsonb_agg(to_jsonb(profile)
                    ORDER BY profile.profile_key)
                    FROM npc_dialogue_profiles profile
                    WHERE profile.revision = @revision),
                'entries', (SELECT jsonb_agg(to_jsonb(entry)
                    ORDER BY entry.profile_key, entry.menu_order)
                    FROM npc_dialogue_profile_entries entry
                    WHERE entry.revision = @revision),
                'bindings', (SELECT jsonb_agg(to_jsonb(binding)
                    ORDER BY binding.npc_key, binding.route_order)
                    FROM npc_dialogue_bindings binding
                    WHERE binding.revision = @revision)
            )::text;
            """);
        AddV14Revision(command);
        var serialized = (string?)await command.ExecuteScalarAsync() ??
            throw new InvalidDataException("Canonical V14 rows are missing.");
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized)));
    }
}
