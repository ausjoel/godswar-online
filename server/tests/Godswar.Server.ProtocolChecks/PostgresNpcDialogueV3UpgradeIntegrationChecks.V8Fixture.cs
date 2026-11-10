using System.Collections.Immutable;
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
    private const string V8FixturePublisher =
        "protocol-check-canonical-v8-fixture";

    private static async Task SeedAndPublishCanonicalV8FixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var texts = await ReadOfficialV8TextsAsync(dataSource);
        AssertCanonicalV8Content(texts, NpcDialogueBaselineV8.CreateRoutes());

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(1193657936, 1448298802);",
                         connection,
                         transaction))
        {
            await locked.ExecuteNonQueryAsync();
        }

        var created = await InsertV8ReleaseAsync(connection, transaction);
        if (created)
        {
            await InsertV8TextsAsync(connection, transaction);
            await InsertV8ProfilesAsync(connection, transaction);
            await InsertV8BindingsAsync(connection, transaction);
        }

        await PublishV8FixtureAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task<bool> InsertV8ReleaseAsync(
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
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV8.ExpectedRevision);
        command.Parameters.AddWithValue(
            "spawnRevision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV8.ExpectedSpawnRevision);
        command.Parameters.AddWithValue(
            "textCount",
            NpcDialogueBaselineV8.ExpectedTextCount);
        command.Parameters.AddWithValue(
            "profileCount",
            NpcDialogueBaselineV8.ExpectedProfileCount);
        command.Parameters.AddWithValue(
            "routeCount",
            NpcDialogueBaselineV8.ExpectedRouteCount);
        command.Parameters.AddWithValue(
            "menuEntryCount",
            NpcDialogueBaselineV8.ExpectedMenuEntryCount);
        command.Parameters.AddWithValue(
            "source",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV8.Source);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task InsertV8TextsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_texts (
                revision, npc_key, scene_key, display_name, description)
            SELECT @revision, text.npc_key, text.scene_key,
                   text.display_name, text.description
            FROM npc_spawn_definitions spawn
            JOIN npc_text_templates text
              ON text.npc_key = spawn.npc_key
            WHERE spawn.revision = @spawnRevision
            ORDER BY text.npc_key;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV8.ExpectedRevision);
        command.Parameters.AddWithValue(
            "spawnRevision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV8.ExpectedSpawnRevision);
        Check.Equal(
            NpcDialogueBaselineV8.ExpectedTextCount,
            await command.ExecuteNonQueryAsync(),
            "canonical V8 fixture text count");
    }

    private static async Task InsertV8ProfilesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var profiles = NpcDialogueBaselineV8.Profiles;
        await using (var command = new NpgsqlCommand(
                         """
                         INSERT INTO npc_dialogue_profiles (
                             revision, profile_key, dialog_index, behavior,
                             initial_request_sub_id)
                         SELECT @revision, input.profile_key,
                                input.dialog_index, input.behavior,
                                input.initial_request_sub_id
                         FROM unnest(
                             @profileKeys, @dialogIndexes, @behaviors,
                             @initialRequestSubIds)
                           AS input(
                               profile_key, dialog_index, behavior,
                               initial_request_sub_id)
                         ORDER BY input.profile_key;
                         """,
                         connection,
                         transaction))
        {
            AddV8Revision(command);
            AddArray(command, "profileKeys", NpgsqlDbType.Text,
                profiles.Select(static profile => profile.ProfileKey)
                    .ToArray());
            AddArray(command, "dialogIndexes", NpgsqlDbType.Integer,
                profiles.Select(static profile => profile.DialogIndex)
                    .ToArray());
            AddArray(command, "behaviors", NpgsqlDbType.Smallint,
                profiles.Select(static profile =>
                        checked((short)profile.Behavior))
                    .ToArray());
            AddArray(command, "initialRequestSubIds", NpgsqlDbType.Integer,
                profiles.Select(static profile =>
                        profile.InitialRequestSubId)
                    .ToArray());
            Check.Equal(
                NpcDialogueBaselineV8.ExpectedProfileCount,
                await command.ExecuteNonQueryAsync(),
                "canonical V8 fixture profile count");
        }

        var entries = profiles
            .SelectMany(profile => profile.InitialMenuSubIds.Select(
                (subId, menuOrder) => new
                {
                    profile.ProfileKey,
                    MenuOrder = checked((short)menuOrder),
                    SubId = subId
                }))
            .ToArray();
        await using var entryCommand = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_profile_entries (
                revision, profile_key, menu_order, sub_id)
            SELECT @revision, input.profile_key,
                   input.menu_order, input.sub_id
            FROM unnest(@profileKeys, @menuOrders, @subIds)
              AS input(profile_key, menu_order, sub_id)
            ORDER BY input.profile_key, input.menu_order;
            """,
            connection,
            transaction);
        AddV8Revision(entryCommand);
        AddArray(entryCommand, "profileKeys", NpgsqlDbType.Text,
            entries.Select(static entry => entry.ProfileKey).ToArray());
        AddArray(entryCommand, "menuOrders", NpgsqlDbType.Smallint,
            entries.Select(static entry => entry.MenuOrder).ToArray());
        AddArray(entryCommand, "subIds", NpgsqlDbType.Integer,
            entries.Select(static entry => entry.SubId).ToArray());
        Check.Equal(
            NpcDialogueBaselineV8.ExpectedMenuEntryCount,
            await entryCommand.ExecuteNonQueryAsync(),
            "canonical V8 fixture menu-entry count");
    }

    private static async Task InsertV8BindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var bindings = NpcDialogueBaselineV8.Bindings;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_bindings (
                revision, npc_key, client_script_key,
                profile_key, route_order)
            SELECT @revision, input.npc_key, input.client_script_key,
                   input.profile_key, input.route_order
            FROM unnest(
                @npcKeys, @clientScriptKeys, @profileKeys, @routeOrders)
              AS input(
                  npc_key, client_script_key, profile_key, route_order)
            ORDER BY input.npc_key, input.route_order;
            """,
            connection,
            transaction);
        AddV8Revision(command);
        AddArray(command, "npcKeys", NpgsqlDbType.Text,
            bindings.Select(static binding => binding.NpcKey).ToArray());
        AddArray(command, "clientScriptKeys", NpgsqlDbType.Text,
            bindings.Select(static binding => binding.ClientScriptKey)
                .ToArray());
        AddArray(command, "profileKeys", NpgsqlDbType.Text,
            bindings.Select(static binding => binding.ProfileKey).ToArray());
        AddArray(command, "routeOrders", NpgsqlDbType.Smallint,
            bindings.Select(static binding =>
                    checked((short)binding.RouteOrder))
                .ToArray());
        Check.Equal(
            NpcDialogueBaselineV8.ExpectedRouteCount,
            await command.ExecuteNonQueryAsync(),
            "canonical V8 fixture route count");
    }

    private static async Task PublishV8FixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_publication (
                family, revision, published_at, publisher)
            VALUES (
                'npc-dialogues', @revision, now(), @publisher)
            ON CONFLICT (family) DO UPDATE
            SET revision = EXCLUDED.revision,
                published_at = EXCLUDED.published_at,
                publisher = EXCLUDED.publisher;
            """,
            connection,
            transaction);
        AddV8Revision(command);
        command.Parameters.AddWithValue(
            "publisher",
            NpgsqlDbType.Varchar,
            V8FixturePublisher);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical V8 fixture publication pointer");
    }

    private static async Task<NpcTextDefinition[]> ReadOfficialV8TextsAsync(
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
            WHERE spawn.revision = @spawnRevision;
            """);
        command.Parameters.AddWithValue(
            "spawnRevision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV8.ExpectedSpawnRevision);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            texts.Add(new NpcTextDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return texts
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddV8Revision(NpgsqlCommand command) =>
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV8.ExpectedRevision);

    private static void AddArray<T>(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType elementType,
        T[] values) =>
        command.Parameters.Add(new NpgsqlParameter(
            name,
            NpgsqlDbType.Array | elementType)
        {
            Value = values
        });

    private static void AssertCanonicalV8Content(
        IReadOnlyList<NpcTextDefinition> texts,
        IReadOnlyList<NpcDialogueRouteDefinition> routes)
    {
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);
        Check.True(
            texts.Count == NpcDialogueBaselineV8.ExpectedTextCount &&
            NpcDialogueBaselineV8.Profiles.Length ==
                NpcDialogueBaselineV8.ExpectedProfileCount &&
            routes.Count == NpcDialogueBaselineV8.ExpectedRouteCount &&
            NpcDialogueBaselineV8.Profiles.Sum(static profile =>
                profile.InitialMenuSubIds.Length) ==
                NpcDialogueBaselineV8.ExpectedMenuEntryCount &&
            revision.EntryCount ==
                NpcDialogueBaselineV8.ExpectedHashedEntryCount &&
            revision.Sha256 == NpcDialogueBaselineV8.ExpectedRevision,
            "fixture data reproduces the exact canonical V8 revision");
    }

    private static async Task<CanonicalV8Snapshot>
        ReadCanonicalV8SnapshotAsync(NpgsqlDataSource dataSource)
    {
        var texts = new List<NpcTextDefinition>();
        var routes = new List<NpcDialogueRouteDefinition>();
        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT npc_key, scene_key,
                                display_name, description
                         FROM npc_dialogue_texts
                         WHERE revision = @revision;
                         """))
        {
            AddV8Revision(command);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                texts.Add(new NpcTextDefinition(
                    reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3)));
            }
        }

        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT binding.npc_key,
                                binding.client_script_key,
                                binding.route_order,
                                profile.dialog_index,
                                profile.behavior,
                                ARRAY_AGG(
                                    entry.sub_id ORDER BY entry.menu_order)
                         FROM npc_dialogue_bindings binding
                         JOIN npc_dialogue_profiles profile
                           ON profile.revision = binding.revision
                          AND profile.profile_key = binding.profile_key
                         JOIN npc_dialogue_profile_entries entry
                           ON entry.revision = profile.revision
                          AND entry.profile_key = profile.profile_key
                         WHERE binding.revision = @revision
                         GROUP BY binding.npc_key,
                                  binding.client_script_key,
                                  binding.route_order,
                                  profile.dialog_index,
                                  profile.behavior;
                         """))
        {
            AddV8Revision(command);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                routes.Add(new NpcDialogueRouteDefinition(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(3),
                    (NpcDialogueBehavior)reader.GetInt16(4),
                    reader.GetFieldValue<int[]>(5).ToImmutableArray())
                {
                    RouteOrder = reader.GetInt16(2)
                });
            }
        }

        var orderedTexts = texts
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var orderedRoutes = routes
            .OrderBy(static route => route.NpcKey, StringComparer.Ordinal)
            .ThenBy(static route => route.RouteOrder)
            .ToArray();
        AssertCanonicalV8Content(orderedTexts, orderedRoutes);
        return new CanonicalV8Snapshot(
            await ReadV8RelationalFingerprintAsync(dataSource));
    }

    private static async Task<string> ReadV8RelationalFingerprintAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT jsonb_build_object(
                'release', (
                    SELECT to_jsonb(release)
                    FROM npc_dialogue_revisions release
                    WHERE release.revision = @revision),
                'texts', COALESCE((
                    SELECT jsonb_agg(to_jsonb(text_row)
                                     ORDER BY text_row.npc_key)
                    FROM npc_dialogue_texts text_row
                    WHERE text_row.revision = @revision), '[]'::jsonb),
                'profiles', COALESCE((
                    SELECT jsonb_agg(to_jsonb(profile)
                                     ORDER BY profile.profile_key)
                    FROM npc_dialogue_profiles profile
                    WHERE profile.revision = @revision), '[]'::jsonb),
                'entries', COALESCE((
                    SELECT jsonb_agg(to_jsonb(entry)
                                     ORDER BY entry.profile_key,
                                              entry.menu_order)
                    FROM npc_dialogue_profile_entries entry
                    WHERE entry.revision = @revision), '[]'::jsonb),
                'bindings', COALESCE((
                    SELECT jsonb_agg(to_jsonb(binding)
                                     ORDER BY binding.npc_key,
                                              binding.route_order)
                    FROM npc_dialogue_bindings binding
                    WHERE binding.revision = @revision), '[]'::jsonb)
            )::text;
            """);
        AddV8Revision(command);
        var serialized = (string?)await command.ExecuteScalarAsync() ??
            throw new InvalidDataException("Canonical V8 rows are missing.");
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized)));
    }

    private sealed record CanonicalV8Snapshot(string RelationalFingerprint);
}
