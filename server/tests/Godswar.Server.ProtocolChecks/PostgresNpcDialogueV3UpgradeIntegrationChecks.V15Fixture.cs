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
    private const string V15FixturePublisher =
        "protocol-check-canonical-v15-fixture";

    private static async Task SeedAndPublishCanonicalV15FixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var texts = NpcDialogueBaselineV15.ApplyTextOverrides(
            await ReadOfficialV15TextsAsync(dataSource));
        var routes = NpcDialogueBaselineV15.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);
        Check.True(
            texts.Length == NpcDialogueBaselineV15.ExpectedTextCount &&
            routes.Length == NpcDialogueBaselineV15.ExpectedRouteCount &&
            revision.EntryCount ==
                NpcDialogueBaselineV15.ExpectedHashedEntryCount &&
            revision.Sha256 == NpcDialogueBaselineV15.ExpectedRevision,
            "fixture data reproduces the exact canonical V15 revision");

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(1193657936, 1448298802);",
                         connection,
                         transaction))
        {
            await locked.ExecuteNonQueryAsync();
        }

        await InsertV15ReleaseAsync(connection, transaction);
        await InsertV15TextsAsync(connection, transaction, texts);
        await CopyV14GeometryAndAddArenaAsync(connection, transaction);
        await PublishV15FixtureAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task<NpcTextDefinition[]> ReadOfficialV15TextsAsync(
        NpgsqlDataSource dataSource)
    {
        var texts = new List<NpcTextDefinition>();
        await using var command = dataSource.CreateCommand(
            """
            SELECT text.npc_key, text.scene_key,
                   text.display_name, text.description
            FROM npc_spawn_definitions spawn
            JOIN npc_text_templates text
              ON text.npc_key = spawn.npc_key
            WHERE spawn.revision = @spawnRevision
            ORDER BY text.npc_key COLLATE "C";
            """);
        command.Parameters.AddWithValue(
            "spawnRevision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV15.ExpectedSpawnRevision);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            texts.Add(new NpcTextDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return texts.ToArray();
    }

    private static async Task InsertV15ReleaseAsync(
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
        AddV15Revision(command);
        command.Parameters.AddWithValue(
            "spawnRevision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV15.ExpectedSpawnRevision);
        command.Parameters.AddWithValue(
            "textCount", NpcDialogueBaselineV15.ExpectedTextCount);
        command.Parameters.AddWithValue(
            "profileCount", NpcDialogueBaselineV15.ExpectedProfileCount);
        command.Parameters.AddWithValue(
            "routeCount", NpcDialogueBaselineV15.ExpectedRouteCount);
        command.Parameters.AddWithValue(
            "menuEntryCount", NpcDialogueBaselineV15.ExpectedMenuEntryCount);
        command.Parameters.AddWithValue(
            "source", NpgsqlDbType.Varchar, NpcDialogueBaselineV15.Source);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical V15 release is inserted");
    }

    private static async Task InsertV15TextsAsync(
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
            AddV15Revision(command);
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
                $"canonical V15 text {text.NpcKey}");
        }
    }

    private static async Task CopyV14GeometryAndAddArenaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_profiles (
                revision, profile_key, dialog_index, behavior,
                initial_request_sub_id)
            SELECT @v15Revision, profile_key, dialog_index, behavior,
                   initial_request_sub_id
            FROM npc_dialogue_profiles
            WHERE revision = @v14Revision;

            INSERT INTO npc_dialogue_profile_entries (
                revision, profile_key, menu_order, sub_id)
            SELECT @v15Revision, profile_key, menu_order, sub_id
            FROM npc_dialogue_profile_entries
            WHERE revision = @v14Revision;

            INSERT INTO npc_dialogue_bindings (
                revision, npc_key, client_script_key, profile_key, route_order)
            SELECT @v15Revision, npc_key, client_script_key,
                   profile_key, route_order
            FROM npc_dialogue_bindings
            WHERE revision = @v14Revision;

            INSERT INTO npc_dialogue_profiles (
                revision, profile_key, dialog_index, behavior,
                initial_request_sub_id)
            VALUES (
                @v15Revision, 'duel_arena_transporter', @dialogIndex,
                @behavior, @initialRequestSubId);

            INSERT INTO npc_dialogue_profile_entries (
                revision, profile_key, menu_order, sub_id)
            VALUES (
                @v15Revision, 'duel_arena_transporter', 0, @travelSubId);

            INSERT INTO npc_dialogue_bindings (
                revision, npc_key, client_script_key, profile_key, route_order)
            VALUES
                (@v15Revision, @doorkeeperKey, @doorkeeperKey,
                 'duel_arena_transporter', 0),
                (@v15Revision, @gatekeeperKey, @gatekeeperKey,
                 'duel_arena_transporter', 0);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "v14Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV14.ExpectedRevision);
        command.Parameters.AddWithValue(
            "v15Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV15.ExpectedRevision);
        command.Parameters.AddWithValue(
            "dialogIndex", DuelArenaTransporterProtocol.DialogIndex);
        command.Parameters.AddWithValue(
            "behavior",
            NpgsqlDbType.Smallint,
            checked((short)NpcDialogueBehavior.DuelArenaTransporter));
        command.Parameters.AddWithValue(
            "initialRequestSubId",
            DuelArenaTransporterProtocol.InitialRequestSubId);
        command.Parameters.AddWithValue(
            "travelSubId", DuelArenaTransporterProtocol.TravelSubId);
        command.Parameters.AddWithValue(
            "doorkeeperKey",
            NpgsqlDbType.Varchar,
            DuelArenaTransporterProtocol.DoorkeeperNpcKey);
        command.Parameters.AddWithValue(
            "gatekeeperKey",
            NpgsqlDbType.Varchar,
            DuelArenaTransporterProtocol.GatekeeperNpcKey);
        Check.Equal(
            NpcDialogueBaselineV15.ExpectedProfileCount +
            NpcDialogueBaselineV15.ExpectedMenuEntryCount +
            NpcDialogueBaselineV15.ExpectedRouteCount,
            await command.ExecuteNonQueryAsync(),
            "canonical V15 geometry extends immutable V14");
    }

    private static async Task PublishV15FixtureAsync(
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
        AddV15Revision(command);
        command.Parameters.AddWithValue(
            "publisher", NpgsqlDbType.Varchar, V15FixturePublisher);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical V15 fixture publication pointer");
    }

    private static void AddV15Revision(NpgsqlCommand command) =>
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV15.ExpectedRevision);

    private static async Task<string> ReadCanonicalV15SnapshotAsync(
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
        AddV15Revision(command);
        var serialized = (string?)await command.ExecuteScalarAsync() ??
            throw new InvalidDataException("Canonical V15 rows are missing.");
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized)));
    }
}
