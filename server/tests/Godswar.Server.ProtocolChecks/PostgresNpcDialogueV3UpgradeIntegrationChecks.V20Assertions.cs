using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private static async Task AssertV20DeltaAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT release.spawn_revision,
                   release.text_count,
                   release.profile_count,
                   release.route_count,
                   release.menu_entry_count,
                   release.source,
                   (SELECT COUNT(*)::integer
                    FROM npc_dialogue_texts text
                    WHERE text.revision = release.revision),
                   (SELECT COUNT(*)::integer
                    FROM npc_dialogue_profiles profile
                    WHERE profile.revision = release.revision),
                   (SELECT COUNT(*)::integer
                    FROM npc_dialogue_bindings binding
                    WHERE binding.revision = release.revision),
                   (SELECT COUNT(*)::integer
                    FROM npc_dialogue_profile_entries entry
                    WHERE entry.revision = release.revision)
            FROM npc_dialogue_revisions release
            WHERE release.revision = @revision;
            """);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV20.ExpectedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "canonical V20 release exists");
        var expectedCounts = new[]
        {
            NpcDialogueBaselineV20.ExpectedTextCount,
            NpcDialogueBaselineV20.ExpectedProfileCount,
            NpcDialogueBaselineV20.ExpectedRouteCount,
            NpcDialogueBaselineV20.ExpectedMenuEntryCount
        };
        Check.True(
            reader.GetString(0) ==
                NpcDialogueBaselineV20.ExpectedSpawnRevision &&
            reader.GetString(5) == NpcDialogueBaselineV20.Source,
            "V20 declares its canonical V7 spawn dependency and source");
        for (var index = 0; index < expectedCounts.Length; index++)
        {
            Check.Equal(
                expectedCounts[index],
                reader.GetInt32(index + 1),
                $"V20 declared count {index}");
            Check.Equal(
                expectedCounts[index],
                reader.GetInt32(index + 6),
                $"V20 stored count {index}");
        }

        await reader.CloseAsync();
        await AssertV20ArenaRosterAsync(dataSource);
    }

    private static async Task AssertV15ToV20DeltaAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
              (SELECT COUNT(*)::integer
               FROM npc_dialogue_texts predecessor
               JOIN npc_dialogue_texts current_text
                 ON current_text.npc_key = predecessor.npc_key
                AND current_text.scene_key = predecessor.scene_key
                AND current_text.display_name = predecessor.display_name
                AND current_text.description = predecessor.description
               WHERE predecessor.revision = @v15
                 AND current_text.revision = @v20),
              (SELECT COUNT(*)::integer
               FROM npc_dialogue_profiles predecessor
               JOIN npc_dialogue_profiles current_profile
                 ON current_profile.profile_key = predecessor.profile_key
                AND current_profile.dialog_index = predecessor.dialog_index
                AND current_profile.behavior = predecessor.behavior
                AND current_profile.initial_request_sub_id =
                    predecessor.initial_request_sub_id
               WHERE predecessor.revision = @v15
                 AND current_profile.revision = @v20),
              (SELECT COUNT(*)::integer
               FROM npc_dialogue_profile_entries predecessor
               JOIN npc_dialogue_profile_entries current_entry
                 ON current_entry.profile_key = predecessor.profile_key
                AND current_entry.menu_order = predecessor.menu_order
                AND current_entry.sub_id = predecessor.sub_id
               WHERE predecessor.revision = @v15
                 AND current_entry.revision = @v20),
              (SELECT COUNT(*)::integer
               FROM npc_dialogue_bindings predecessor
               JOIN npc_dialogue_bindings current_binding
                 ON current_binding.npc_key = predecessor.npc_key
                AND current_binding.route_order = predecessor.route_order
                AND current_binding.client_script_key =
                    predecessor.client_script_key
                AND current_binding.profile_key = predecessor.profile_key
               WHERE predecessor.revision = @v15
                 AND current_binding.revision = @v20),
              (SELECT COUNT(*)::integer
               FROM npc_dialogue_texts current_text
               LEFT JOIN npc_dialogue_texts predecessor
                 ON predecessor.revision = @v15
                AND predecessor.npc_key = current_text.npc_key
               WHERE current_text.revision = @v20
                 AND predecessor.npc_key IS NULL);
            """);
        AddV15AndV20Revisions(command);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt32(0) ==
                NpcDialogueBaselineV15.ExpectedTextCount &&
            reader.GetInt32(1) ==
                NpcDialogueBaselineV15.ExpectedProfileCount - 1 &&
            reader.GetInt32(2) ==
                NpcDialogueBaselineV15.ExpectedMenuEntryCount - 1 &&
            reader.GetInt32(3) ==
                NpcDialogueBaselineV15.ExpectedRouteCount - 2 &&
            reader.GetInt32(4) == 5,
            "V20 preserves V15 stock texts, replaces the legacy Arena " +
            "profile and two bindings, and adds five captured actor texts");
    }

    private static async Task AssertV20ArenaRosterAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT spawn.npc_key,
                   spawn.template_key,
                   spawn.object_id,
                   spawn.pos_x,
                   spawn.pos_z,
                   text.display_name,
                   LENGTH(text.description) > 0,
                   (SELECT COUNT(*)::integer
                    FROM npc_dialogue_bindings binding
                    WHERE binding.revision = text.revision
                      AND binding.npc_key = text.npc_key)
            FROM npc_spawn_definitions spawn
            JOIN npc_dialogue_texts text
              ON text.revision = @dialogue_revision
             AND text.npc_key = spawn.npc_key
            WHERE spawn.revision = @spawn_revision
              AND spawn.map_id = 57
            ORDER BY spawn.npc_key COLLATE "C";
            """);
        command.Parameters.AddWithValue(
            "dialogue_revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV20.ExpectedRevision);
        command.Parameters.AddWithValue(
            "spawn_revision",
            NpgsqlDbType.Varchar,
            NpcContentBaselineV7.ExpectedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        var expected = NpcContentBaselineV7.LoadDefinitions()
            .Where(static npc =>
                npc.MapId == DuelArenaTransporterProtocol.MapId)
            .OrderBy(static npc => npc.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var index = 0;
        while (await reader.ReadAsync())
        {
            Check.True(index < expected.Length, "V20 has no extra Arena NPC");
            var actor = expected[index++];
            var routeCount = DuelArenaCapturedTransportProtocol.IsCapturedEndpoint(
                actor.NpcKey,
                actor.InteractionId) ? 1 : 0;
            Check.True(
                reader.GetString(0) == actor.NpcKey &&
                reader.GetString(1) == actor.TemplateKey &&
                reader.GetInt64(2) == actor.ObjectId &&
                reader.GetFloat(3) == actor.X &&
                reader.GetFloat(4) == actor.Z &&
                !string.IsNullOrWhiteSpace(reader.GetString(5)) &&
                reader.GetBoolean(6) &&
                reader.GetInt32(7) == routeCount,
                $"V20 persists exact Arena actor {actor.NpcKey}");
        }

        Check.Equal(7, index, "V20 persists all seven captured Arena actors");
    }

    private static void AddV15AndV20Revisions(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue(
            "v15",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV15.ExpectedRevision);
        command.Parameters.AddWithValue(
            "v20",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV20.ExpectedRevision);
    }
}
