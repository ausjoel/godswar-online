using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private static async Task CheckLegacyV21ReleaseUpgradeAsync(
        NpgsqlDataSource dataSource, string connectionString)
    {
        var payload = new WorldContentFamilyRevision("npc-dialogues",
            NpcDialogueBaselineV21.ExpectedRevision,
            NpcDialogueBaselineV21.ExpectedHashedEntryCount);
        var dependencyBound = WorldContentRevisionHasher.HashNpcDialogueRelease(
            payload, NpcDialogueBaselineV21.ExpectedSpawnRevision).Sha256;
        foreach (var previousRevision in new[] { payload.Sha256, dependencyBound })
        {
            await SeedPreviousDialogueReleaseAsync(dataSource, previousRevision,
                NpcDialogueBaselineV21.Source, NpcDialogueBaselineV14.InstanceCallerDescription);
            var legacy = await PostgresWorldContentReaderLoader.LoadAsync(connectionString);
            Check.Equal(NpcDialogueBaselineV21.ExpectedRevision,
                legacy.Manifest.NpcDialogues.Sha256,
                "loader retains both historical V21 publication identities");
            var before = await ReadDialogueReleaseSnapshotAsync(
                dataSource, previousRevision);
            var upgraded = await PostgresNpcDialogueBaselinePublisher
                .EnsurePublishedAsync(connectionString);
            Check.True(upgraded.Created && upgraded.Revision ==
                PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision,
                "V21 publication promotes to dependency-bound V23");
            Check.Equal(before, await ReadDialogueReleaseSnapshotAsync(
                    dataSource, previousRevision),
                "V23 promotion leaves every immutable V21 row unchanged");
            var current = await PostgresWorldContentReaderLoader.LoadAsync(connectionString);
            Check.Equal(NpcDialogueBaselineV23.ExpectedRevision,
                current.Manifest.NpcDialogues.Sha256,
                "upgraded loader pins the new V23 dialogue payload");
            await AssertV21ToV23TextDeltaAsync(dataSource, previousRevision);
            var repeated = await PostgresNpcDialogueBaselinePublisher
                .EnsurePublishedAsync(connectionString);
            Check.True(!repeated.Created,
                "V23 publication is idempotent after either V21 predecessor");
        }
    }

    private static async Task SeedPreviousDialogueReleaseAsync(
        NpgsqlDataSource dataSource, string previousRevision,
        string source, string description)
    {
        // V23 retained all V21/V22 geometry. Restore the two old descriptions and
        // source while inserting a fresh predecessor; never update sealed rows.
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO npc_dialogue_revisions
                (revision, spawn_revision, text_count, profile_count,
                 route_count, menu_entry_count, source)
            SELECT @previous, spawn_revision, text_count, profile_count,
                   route_count, menu_entry_count, @source
            FROM npc_dialogue_revisions WHERE revision = @release;
            INSERT INTO npc_dialogue_texts
                (revision, npc_key, scene_key, display_name, description)
            SELECT @previous, npc_key, scene_key, display_name,
                   CASE WHEN npc_key IN ('Athens_060', 'Sparta_060')
                        THEN @description ELSE description END
            FROM npc_dialogue_texts WHERE revision = @release;
            INSERT INTO npc_dialogue_profiles
                (revision, profile_key, dialog_index, behavior, initial_request_sub_id)
            SELECT @previous, profile_key, dialog_index, behavior, initial_request_sub_id
            FROM npc_dialogue_profiles WHERE revision = @release;
            INSERT INTO npc_dialogue_profile_entries
                (revision, profile_key, menu_order, sub_id)
            SELECT @previous, profile_key, menu_order, sub_id
            FROM npc_dialogue_profile_entries WHERE revision = @release;
            INSERT INTO npc_dialogue_bindings
                (revision, npc_key, client_script_key, profile_key, route_order)
            SELECT @previous, npc_key, client_script_key, profile_key, route_order
            FROM npc_dialogue_bindings WHERE revision = @release;
            UPDATE npc_dialogue_publication SET revision = @previous,
                publisher = 'protocol-check-dialogue-predecessor', published_at = now()
            WHERE family = 'npc-dialogues';
            """);
        command.Parameters.AddWithValue("previous", NpgsqlDbType.Varchar, previousRevision);
        command.Parameters.AddWithValue("release", NpgsqlDbType.Varchar,
            PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("description", description);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertV21ToV23TextDeltaAsync(
        NpgsqlDataSource dataSource, string previousRevision)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT ARRAY_AGG(current_text.npc_key ORDER BY current_text.npc_key COLLATE "C")
                       FILTER (WHERE current_text.description <> previous.description),
                   COUNT(*) FILTER (WHERE current_text.scene_key = previous.scene_key
                       AND current_text.display_name = previous.display_name)
            FROM npc_dialogue_texts previous
            JOIN npc_dialogue_texts current_text USING (npc_key)
            WHERE previous.revision = @previous AND current_text.revision = @current;
            """);
        command.Parameters.AddWithValue("previous", previousRevision);
        command.Parameters.AddWithValue("current",
            PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync() &&
            reader.GetFieldValue<string[]>(0).SequenceEqual(["Athens_060", "Sparta_060"]) &&
            reader.GetInt64(1) == NpcDialogueBaselineV21.ExpectedTextCount,
            "V21-to-V23 publication changes only the two capital descriptions");
    }

    private static async Task<string> ReadDialogueReleaseSnapshotAsync(
        NpgsqlDataSource dataSource, string revision)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT md5(string_agg(row_json::text, E'\n' ORDER BY row_json::text))
            FROM (
                SELECT to_jsonb(r) AS row_json FROM npc_dialogue_revisions r WHERE revision = @revision
                UNION ALL
                SELECT to_jsonb(r) FROM npc_dialogue_texts r WHERE revision = @revision
                UNION ALL
                SELECT to_jsonb(r) FROM npc_dialogue_profiles r WHERE revision = @revision
                UNION ALL
                SELECT to_jsonb(r) FROM npc_dialogue_profile_entries r WHERE revision = @revision
                UNION ALL
                SELECT to_jsonb(r) FROM npc_dialogue_bindings r WHERE revision = @revision
            ) rows;
            """);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, revision);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
