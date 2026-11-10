using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private const string V10FixturePublisher =
        "protocol-check-canonical-v10-fixture";

    private static async Task SeedAndPublishCanonicalV10FixtureAsync(
        NpgsqlDataSource dataSource)
    {
        await SeedAndPublishCanonicalV9FixtureAsync(dataSource);
        var texts = NpcDialogueBaselineV10.ApplyTextOverrides(
            await ReadOfficialV8TextsAsync(dataSource));
        var routes = NpcDialogueBaselineV10.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);
        Check.True(
            texts.Length == NpcDialogueBaselineV10.ExpectedTextCount &&
            routes.Length == NpcDialogueBaselineV10.ExpectedRouteCount &&
            revision.EntryCount ==
                NpcDialogueBaselineV10.ExpectedHashedEntryCount &&
            revision.Sha256 == NpcDialogueBaselineV10.ExpectedRevision,
            "fixture data reproduces the exact canonical V10 revision");

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(1193657936, 1448298802);",
                         connection,
                         transaction))
        {
            await locked.ExecuteNonQueryAsync();
        }

        if (await InsertV10ReleaseAsync(connection, transaction))
        {
            await CopyV10TextsAsync(connection, transaction);
            await CopyV10GeometryAsync(connection, transaction);
        }

        await PublishV10FixtureAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task<bool> InsertV10ReleaseAsync(
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
                @routeCount, @menuEntryCount, @source)
            ON CONFLICT (revision) DO NOTHING
            RETURNING true;
            """,
            connection,
            transaction);
        AddV10Revision(command);
        command.Parameters.AddWithValue(
            "spawnRevision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV10.ExpectedSpawnRevision);
        command.Parameters.AddWithValue(
            "textCount", NpcDialogueBaselineV10.ExpectedTextCount);
        command.Parameters.AddWithValue(
            "profileCount", NpcDialogueBaselineV10.ExpectedProfileCount);
        command.Parameters.AddWithValue(
            "routeCount", NpcDialogueBaselineV10.ExpectedRouteCount);
        command.Parameters.AddWithValue(
            "menuEntryCount", NpcDialogueBaselineV10.ExpectedMenuEntryCount);
        command.Parameters.AddWithValue(
            "source", NpgsqlDbType.Varchar, NpcDialogueBaselineV10.Source);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task CopyV10TextsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_texts (
                revision, npc_key, scene_key, display_name, description)
            SELECT @revision,
                   npc_key,
                   scene_key,
                   display_name,
                   CASE
                       WHEN npc_key IN ('Athens_142', 'Sparta_142')
                           THEN @levelSealerDescription
                       ELSE description
                   END
            FROM npc_dialogue_texts
            WHERE revision = @v9Revision
            ORDER BY npc_key;
            """,
            connection,
            transaction);
        AddV10Revision(command);
        command.Parameters.AddWithValue(
            "v9Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV9.ExpectedRevision);
        command.Parameters.AddWithValue(
            "levelSealerDescription",
            NpgsqlDbType.Text,
            NpcDialogueBaselineV10.LevelSealerDescription);
        Check.Equal(
            NpcDialogueBaselineV10.ExpectedTextCount,
            await command.ExecuteNonQueryAsync(),
            "canonical V10 fixture text count");
    }

    private static async Task CopyV10GeometryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await CopyV10RowsAsync(
            connection,
            transaction,
            """
            INSERT INTO npc_dialogue_profiles (
                revision, profile_key, dialog_index, behavior,
                initial_request_sub_id)
            SELECT @revision, profile_key, dialog_index, behavior,
                   initial_request_sub_id
            FROM npc_dialogue_profiles
            WHERE revision = @v9Revision
            ORDER BY profile_key;
            """,
            NpcDialogueBaselineV10.ExpectedProfileCount,
            "profile");
        await CopyV10RowsAsync(
            connection,
            transaction,
            """
            INSERT INTO npc_dialogue_profile_entries (
                revision, profile_key, menu_order, sub_id)
            SELECT @revision, profile_key, menu_order, sub_id
            FROM npc_dialogue_profile_entries
            WHERE revision = @v9Revision
            ORDER BY profile_key, menu_order;
            """,
            NpcDialogueBaselineV10.ExpectedMenuEntryCount,
            "menu entry");
        await CopyV10RowsAsync(
            connection,
            transaction,
            """
            INSERT INTO npc_dialogue_bindings (
                revision, npc_key, client_script_key,
                profile_key, route_order)
            SELECT @revision, npc_key, client_script_key,
                   profile_key, route_order
            FROM npc_dialogue_bindings
            WHERE revision = @v9Revision
            ORDER BY npc_key, route_order;
            """,
            NpcDialogueBaselineV10.ExpectedRouteCount,
            "binding");
    }

    private static async Task CopyV10RowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        int expectedCount,
        string family)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddV10Revision(command);
        command.Parameters.AddWithValue(
            "v9Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV9.ExpectedRevision);
        Check.Equal(
            expectedCount,
            await command.ExecuteNonQueryAsync(),
            $"canonical V10 fixture {family} count");
    }

    private static async Task PublishV10FixtureAsync(
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
        AddV10Revision(command);
        command.Parameters.AddWithValue(
            "publisher", NpgsqlDbType.Varchar, V10FixturePublisher);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical V10 fixture publication pointer");
    }

    private static void AddV10Revision(NpgsqlCommand command) =>
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV10.ExpectedRevision);

    private static async Task<string> ReadCanonicalV10SnapshotAsync(
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
        AddV10Revision(command);
        var serialized = (string?)await command.ExecuteScalarAsync() ??
            throw new InvalidDataException("Canonical V10 rows are missing.");
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized)));
    }
}
