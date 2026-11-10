using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private const string V9FixturePublisher =
        "protocol-check-canonical-v9-fixture";

    private static async Task SeedAndPublishCanonicalV9FixtureAsync(
        NpgsqlDataSource dataSource)
    {
        await SeedAndPublishCanonicalV8FixtureAsync(dataSource);
        var texts = await ReadOfficialV8TextsAsync(dataSource);
        var routes = NpcDialogueBaselineV9.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);
        Check.True(
            texts.Length == NpcDialogueBaselineV9.ExpectedTextCount &&
            routes.Length == NpcDialogueBaselineV9.ExpectedRouteCount &&
            revision.EntryCount ==
                NpcDialogueBaselineV9.ExpectedHashedEntryCount &&
            revision.Sha256 == NpcDialogueBaselineV9.ExpectedRevision,
            "fixture data reproduces the exact canonical V9 revision");

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(1193657936, 1448298802);",
                         connection,
                         transaction))
        {
            await locked.ExecuteNonQueryAsync();
        }

        if (await InsertV9ReleaseAsync(connection, transaction))
        {
            await CopyV9TextsAsync(connection, transaction);
            await InsertV9ProfilesAsync(connection, transaction);
            await InsertV9BindingsAsync(connection, transaction);
        }

        await PublishV9FixtureAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task<bool> InsertV9ReleaseAsync(
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
        AddV9Revision(command);
        command.Parameters.AddWithValue(
            "spawnRevision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV9.ExpectedSpawnRevision);
        command.Parameters.AddWithValue(
            "textCount",
            NpcDialogueBaselineV9.ExpectedTextCount);
        command.Parameters.AddWithValue(
            "profileCount",
            NpcDialogueBaselineV9.ExpectedProfileCount);
        command.Parameters.AddWithValue(
            "routeCount",
            NpcDialogueBaselineV9.ExpectedRouteCount);
        command.Parameters.AddWithValue(
            "menuEntryCount",
            NpcDialogueBaselineV9.ExpectedMenuEntryCount);
        command.Parameters.AddWithValue(
            "source",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV9.Source);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task CopyV9TextsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_texts (
                revision, npc_key, scene_key, display_name, description)
            SELECT @revision, npc_key, scene_key, display_name, description
            FROM npc_dialogue_texts
            WHERE revision = @v8Revision
            ORDER BY npc_key;
            """,
            connection,
            transaction);
        AddV9Revision(command);
        command.Parameters.AddWithValue(
            "v8Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV8.ExpectedRevision);
        Check.Equal(
            NpcDialogueBaselineV9.ExpectedTextCount,
            await command.ExecuteNonQueryAsync(),
            "canonical V9 fixture text count");
    }

    private static async Task InsertV9ProfilesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var profileCommand = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_profiles (
                revision, profile_key, dialog_index, behavior,
                initial_request_sub_id)
            VALUES (
                @revision, @profileKey, @dialogIndex, @behavior,
                @initialRequestSubId);
            """,
            connection,
            transaction);
        await using var entryCommand = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_profile_entries (
                revision, profile_key, menu_order, sub_id)
            VALUES (@revision, @profileKey, @menuOrder, @subId);
            """,
            connection,
            transaction);
        foreach (var profile in NpcDialogueBaselineV9.Profiles)
        {
            profileCommand.Parameters.Clear();
            AddV9Revision(profileCommand);
            profileCommand.Parameters.AddWithValue(
                "profileKey", NpgsqlDbType.Varchar, profile.ProfileKey);
            profileCommand.Parameters.AddWithValue(
                "dialogIndex", NpgsqlDbType.Integer, profile.DialogIndex);
            profileCommand.Parameters.AddWithValue(
                "behavior",
                NpgsqlDbType.Smallint,
                checked((short)profile.Behavior));
            profileCommand.Parameters.AddWithValue(
                "initialRequestSubId",
                NpgsqlDbType.Integer,
                profile.InitialRequestSubId);
            Check.Equal(
                1,
                await profileCommand.ExecuteNonQueryAsync(),
                $"canonical V9 profile {profile.ProfileKey}");

            for (var index = 0;
                 index < profile.InitialMenuSubIds.Length;
                 index++)
            {
                entryCommand.Parameters.Clear();
                AddV9Revision(entryCommand);
                entryCommand.Parameters.AddWithValue(
                    "profileKey", NpgsqlDbType.Varchar, profile.ProfileKey);
                entryCommand.Parameters.AddWithValue(
                    "menuOrder", NpgsqlDbType.Smallint, checked((short)index));
                entryCommand.Parameters.AddWithValue(
                    "subId",
                    NpgsqlDbType.Integer,
                    profile.InitialMenuSubIds[index]);
                Check.Equal(
                    1,
                    await entryCommand.ExecuteNonQueryAsync(),
                    $"canonical V9 menu {profile.ProfileKey}/{index}");
            }
        }
    }

    private static async Task InsertV9BindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_bindings (
                revision, npc_key, client_script_key,
                profile_key, route_order)
            VALUES (
                @revision, @npcKey, @clientScriptKey,
                @profileKey, @routeOrder);
            """,
            connection,
            transaction);
        foreach (var binding in NpcDialogueBaselineV9.Bindings)
        {
            command.Parameters.Clear();
            AddV9Revision(command);
            command.Parameters.AddWithValue(
                "npcKey", NpgsqlDbType.Varchar, binding.NpcKey);
            command.Parameters.AddWithValue(
                "clientScriptKey",
                NpgsqlDbType.Varchar,
                binding.ClientScriptKey);
            command.Parameters.AddWithValue(
                "profileKey", NpgsqlDbType.Varchar, binding.ProfileKey);
            command.Parameters.AddWithValue(
                "routeOrder",
                NpgsqlDbType.Smallint,
                checked((short)binding.RouteOrder));
            Check.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                $"canonical V9 binding {binding.NpcKey}");
        }
    }

    private static async Task PublishV9FixtureAsync(
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
        AddV9Revision(command);
        command.Parameters.AddWithValue(
            "publisher", NpgsqlDbType.Varchar, V9FixturePublisher);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical V9 fixture publication pointer");
    }

    private static void AddV9Revision(NpgsqlCommand command) =>
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV9.ExpectedRevision);

    private static async Task<string> ReadCanonicalV9SnapshotAsync(
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
        AddV9Revision(command);
        var serialized = (string?)await command.ExecuteScalarAsync() ??
            throw new InvalidDataException("Canonical V9 rows are missing.");
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized)));
    }
}
