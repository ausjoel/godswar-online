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
    private const string V13FixturePublisher =
        "protocol-check-canonical-v13-fixture";

    private static async Task SeedAndPublishCanonicalV13FixtureAsync(
        NpgsqlDataSource dataSource)
    {
        await SeedAndPublishCanonicalV10FixtureAsync(dataSource);
        var texts = NpcDialogueBaselineV13.ApplyTextOverrides(
            await ReadOfficialV8TextsAsync(dataSource));
        var routes = NpcDialogueBaselineV13.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);
        Check.True(
            texts.Length == NpcDialogueBaselineV13.ExpectedTextCount &&
            routes.Length == NpcDialogueBaselineV13.ExpectedRouteCount &&
            revision.EntryCount ==
                NpcDialogueBaselineV13.ExpectedHashedEntryCount &&
            revision.Sha256 == NpcDialogueBaselineV13.ExpectedRevision,
            "fixture data reproduces the exact canonical V13 revision");

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(1193657936, 1448298802);",
                         connection,
                         transaction))
        {
            await locked.ExecuteNonQueryAsync();
        }

        if (await InsertV13ReleaseAsync(connection, transaction))
        {
            await InsertV13TextsAsync(connection, transaction, texts);
            await InsertV13GeometryAsync(connection, transaction);
        }

        await PublishV13FixtureAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task<bool> InsertV13ReleaseAsync(
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
        AddV13Revision(command);
        command.Parameters.AddWithValue(
            "spawnRevision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV13.ExpectedSpawnRevision);
        command.Parameters.AddWithValue(
            "textCount", NpcDialogueBaselineV13.ExpectedTextCount);
        command.Parameters.AddWithValue(
            "profileCount", NpcDialogueBaselineV13.ExpectedProfileCount);
        command.Parameters.AddWithValue(
            "routeCount", NpcDialogueBaselineV13.ExpectedRouteCount);
        command.Parameters.AddWithValue(
            "menuEntryCount", NpcDialogueBaselineV13.ExpectedMenuEntryCount);
        command.Parameters.AddWithValue(
            "source", NpgsqlDbType.Varchar, NpcDialogueBaselineV13.Source);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task InsertV13TextsAsync(
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
            AddV13Revision(command);
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
                $"canonical V13 text {text.NpcKey}");
        }
    }

    private static async Task InsertV13GeometryAsync(
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
        foreach (var profile in NpcDialogueBaselineV13.Profiles)
        {
            profileCommand.Parameters.Clear();
            AddV13Revision(profileCommand);
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
                $"canonical V13 profile {profile.ProfileKey}");

            for (var index = 0;
                 index < profile.InitialMenuSubIds.Length;
                 index++)
            {
                entryCommand.Parameters.Clear();
                AddV13Revision(entryCommand);
                entryCommand.Parameters.AddWithValue(
                    "profileKey", NpgsqlDbType.Varchar, profile.ProfileKey);
                entryCommand.Parameters.AddWithValue(
                    "menuOrder", NpgsqlDbType.Smallint, checked((short)index));
                entryCommand.Parameters.AddWithValue(
                    "subId", NpgsqlDbType.Integer, profile.InitialMenuSubIds[index]);
                Check.Equal(
                    1,
                    await entryCommand.ExecuteNonQueryAsync(),
                    $"canonical V13 menu {profile.ProfileKey}/{index}");
            }
        }

        await using var bindingCommand = new NpgsqlCommand(
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
        foreach (var binding in NpcDialogueBaselineV13.Bindings)
        {
            bindingCommand.Parameters.Clear();
            AddV13Revision(bindingCommand);
            bindingCommand.Parameters.AddWithValue(
                "npcKey", NpgsqlDbType.Varchar, binding.NpcKey);
            bindingCommand.Parameters.AddWithValue(
                "clientScriptKey",
                NpgsqlDbType.Varchar,
                binding.ClientScriptKey);
            bindingCommand.Parameters.AddWithValue(
                "profileKey", NpgsqlDbType.Varchar, binding.ProfileKey);
            bindingCommand.Parameters.AddWithValue(
                "routeOrder",
                NpgsqlDbType.Smallint,
                checked((short)binding.RouteOrder));
            Check.Equal(
                1,
                await bindingCommand.ExecuteNonQueryAsync(),
                $"canonical V13 binding {binding.NpcKey}");
        }
    }

    private static async Task PublishV13FixtureAsync(
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
        AddV13Revision(command);
        command.Parameters.AddWithValue(
            "publisher", NpgsqlDbType.Varchar, V13FixturePublisher);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical V13 fixture publication pointer");
    }

    private static void AddV13Revision(NpgsqlCommand command) =>
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV13.ExpectedRevision);

    private static async Task<string> ReadCanonicalV13SnapshotAsync(
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
        AddV13Revision(command);
        var serialized = (string?)await command.ExecuteScalarAsync() ??
            throw new InvalidDataException("Canonical V13 rows are missing.");
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized)));
    }
}
