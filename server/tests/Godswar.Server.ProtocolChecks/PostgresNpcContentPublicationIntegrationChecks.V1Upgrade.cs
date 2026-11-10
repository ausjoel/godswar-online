using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresNpcContentPublicationIntegrationChecks
{
    private const string V1FixturePublisher =
        "protocol-check-canonical-npc-v1-fixture";

    internal static async Task SeedAndPublishCanonicalV1FixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var definitions = NpcContentBaselineV1.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);
        Check.True(
            definitions.Length == NpcContentBaselineV1.ExpectedEntryCount &&
            revision.EntryCount == NpcContentBaselineV1.ExpectedEntryCount &&
            revision.Sha256 == NpcContentBaselineV1.ExpectedRevision,
            "fixture data reproduces the exact canonical NPC V1 revision");

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(1193657936, 1448298801);",
                         connection,
                         transaction))
        {
            await locked.ExecuteNonQueryAsync();
        }

        await InsertV1ReleaseAsync(connection, transaction);
        await InsertV1DefinitionsAsync(
            connection,
            transaction,
            definitions);
        await PublishV1FixtureAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task InsertV1ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_content_revisions (
                revision, entry_count, source)
            VALUES (@revision, @entryCount, @source);
            """,
            connection,
            transaction);
        AddV1Revision(command);
        command.Parameters.AddWithValue(
            "entryCount", NpcContentBaselineV1.ExpectedEntryCount);
        command.Parameters.AddWithValue(
            "source", NpgsqlDbType.Varchar, NpcContentBaselineV1.Source);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical NPC V1 release is inserted");
    }

    private static async Task InsertV1DefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_spawn_definitions (
                revision, map_id, scene_key, npc_key, template_key,
                object_id, pos_x, pos_z, interaction_id, appearance_type,
                facing, detail_10077, detail_10080)
            VALUES (
                @revision, @mapId, @sceneKey, @npcKey, @templateKey,
                @objectId, @posX, @posZ, @interactionId, @appearanceType,
                @facing, @detail10077, @detail10080);
            """,
            connection,
            transaction);
        foreach (var definition in definitions)
        {
            command.Parameters.Clear();
            AddV1Revision(command);
            command.Parameters.AddWithValue(
                "mapId", NpgsqlDbType.Smallint, definition.MapId);
            command.Parameters.AddWithValue(
                "sceneKey", NpgsqlDbType.Varchar, definition.SceneKey);
            command.Parameters.AddWithValue(
                "npcKey", NpgsqlDbType.Varchar, definition.NpcKey);
            command.Parameters.AddWithValue(
                "templateKey", NpgsqlDbType.Varchar, definition.TemplateKey);
            command.Parameters.AddWithValue(
                "objectId", NpgsqlDbType.Bigint, checked((long)definition.ObjectId));
            command.Parameters.AddWithValue(
                "posX", NpgsqlDbType.Real, definition.X);
            command.Parameters.AddWithValue(
                "posZ", NpgsqlDbType.Real, definition.Z);
            command.Parameters.AddWithValue(
                "interactionId",
                NpgsqlDbType.Bigint,
                checked((long)definition.InteractionId));
            command.Parameters.AddWithValue(
                "appearanceType",
                NpgsqlDbType.Bigint,
                checked((long)definition.AppearanceType));
            command.Parameters.AddWithValue(
                "facing", NpgsqlDbType.Real, definition.Facing);
            command.Parameters.AddWithValue(
                "detail10077", NpgsqlDbType.Bytea, definition.Detail10077);
            command.Parameters.AddWithValue(
                "detail10080", NpgsqlDbType.Bytea, definition.Detail10080);
            Check.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                $"canonical NPC V1 definition {definition.NpcKey}");
        }
    }

    private static async Task PublishV1FixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_content_publication (
                family, revision, published_at, publisher)
            VALUES ('npcs', @revision, now(), @publisher);
            """,
            connection,
            transaction);
        AddV1Revision(command);
        command.Parameters.AddWithValue(
            "publisher", NpgsqlDbType.Varchar, V1FixturePublisher);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical NPC V1 fixture is published");
    }

    private static void AddV1Revision(NpgsqlCommand command) =>
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcContentBaselineV1.ExpectedRevision);

    private static async Task<string> ReadCanonicalV1SnapshotAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT jsonb_build_object(
                'release', (SELECT to_jsonb(release)
                    FROM npc_content_revisions release
                    WHERE release.revision = @revision),
                'definitions', (SELECT jsonb_agg(to_jsonb(definition)
                    ORDER BY definition.map_id,
                             definition.npc_key COLLATE "C",
                             definition.template_key COLLATE "C",
                             definition.object_id)
                    FROM npc_spawn_definitions definition
                    WHERE definition.revision = @revision)
            )::text;
            """);
        AddV1Revision(command);
        var serialized = (string?)await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "Canonical NPC V1 rows are missing.");
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized)));
    }
}
