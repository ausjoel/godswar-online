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
    private const string V2FixturePublisher =
        "protocol-check-canonical-npc-v2-fixture";

    internal static async Task SeedAndPublishCanonicalV2FixtureAsync(
        NpgsqlDataSource dataSource)
    {
        await SeedAndPublishCanonicalV1FixtureAsync(dataSource);

        await PublishCanonicalV2FixtureAsync(dataSource);
    }

    internal static async Task PublishCanonicalV2FixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var definitions = NpcContentBaselineV2.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);
        Check.True(
            definitions.Length == NpcContentBaselineV2.ExpectedEntryCount &&
            revision.EntryCount == NpcContentBaselineV2.ExpectedEntryCount &&
            revision.Sha256 == NpcContentBaselineV2.ExpectedRevision,
            "fixture data reproduces the exact canonical NPC V2 revision");

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(1193657936, 1448298801);",
                         connection,
                         transaction))
        {
            await locked.ExecuteNonQueryAsync();
        }

        await InsertV2ReleaseAsync(connection, transaction);
        await InsertV2DefinitionsAsync(
            connection,
            transaction,
            definitions);
        await PublishV2FixtureAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task InsertV2ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_content_revisions (
                revision, entry_count, source)
            VALUES (@revision, @entry_count, @source);
            """,
            connection,
            transaction);
        AddV2Revision(command);
        command.Parameters.AddWithValue(
            "entry_count",
            NpgsqlDbType.Integer,
            NpcContentBaselineV2.ExpectedEntryCount);
        command.Parameters.AddWithValue(
            "source",
            NpgsqlDbType.Varchar,
            NpcContentBaselineV2.Source);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical NPC V2 release is inserted");
    }

    private static async Task InsertV2DefinitionsAsync(
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
                @revision, @map_id, @scene_key, @npc_key, @template_key,
                @object_id, @pos_x, @pos_z, @interaction_id,
                @appearance_type, @facing, @detail_10077, @detail_10080);
            """,
            connection,
            transaction);
        foreach (var definition in definitions)
        {
            command.Parameters.Clear();
            AddV2Revision(command);
            command.Parameters.AddWithValue(
                "map_id", NpgsqlDbType.Smallint, definition.MapId);
            command.Parameters.AddWithValue(
                "scene_key", NpgsqlDbType.Varchar, definition.SceneKey);
            command.Parameters.AddWithValue(
                "npc_key", NpgsqlDbType.Varchar, definition.NpcKey);
            command.Parameters.AddWithValue(
                "template_key", NpgsqlDbType.Varchar, definition.TemplateKey);
            command.Parameters.AddWithValue(
                "object_id",
                NpgsqlDbType.Bigint,
                checked((long)definition.ObjectId));
            command.Parameters.AddWithValue(
                "pos_x", NpgsqlDbType.Real, definition.X);
            command.Parameters.AddWithValue(
                "pos_z", NpgsqlDbType.Real, definition.Z);
            command.Parameters.AddWithValue(
                "interaction_id",
                NpgsqlDbType.Bigint,
                checked((long)definition.InteractionId));
            command.Parameters.AddWithValue(
                "appearance_type",
                NpgsqlDbType.Bigint,
                checked((long)definition.AppearanceType));
            command.Parameters.AddWithValue(
                "facing", NpgsqlDbType.Real, definition.Facing);
            command.Parameters.AddWithValue(
                "detail_10077",
                NpgsqlDbType.Bytea,
                definition.Detail10077);
            command.Parameters.AddWithValue(
                "detail_10080",
                NpgsqlDbType.Bytea,
                definition.Detail10080);
            Check.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                $"canonical NPC V2 definition {definition.NpcKey}");
        }
    }

    private static async Task PublishV2FixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE npc_content_publication
            SET revision = @revision,
                published_at = now(),
                publisher = @publisher
            WHERE family = 'npcs';
            """,
            connection,
            transaction);
        AddV2Revision(command);
        command.Parameters.AddWithValue(
            "publisher", NpgsqlDbType.Varchar, V2FixturePublisher);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "canonical NPC V2 fixture is published");
    }

    private static void AddV2Revision(NpgsqlCommand command) =>
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcContentBaselineV2.ExpectedRevision);

    private static async Task<string> ReadCanonicalV2SnapshotAsync(
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
        AddV2Revision(command);
        var serialized = (string?)await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "Canonical NPC V2 rows are missing.");
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized)));
    }
}
