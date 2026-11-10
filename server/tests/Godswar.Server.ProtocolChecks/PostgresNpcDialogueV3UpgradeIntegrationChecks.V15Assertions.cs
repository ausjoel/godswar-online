using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private static async Task AssertV15DeltaAsync(
        NpgsqlDataSource dataSource)
    {
        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT release.spawn_revision,
                                release.text_count,
                                release.profile_count,
                                release.route_count,
                                release.menu_entry_count,
                                release.source,
                                (
                                    SELECT COUNT(*)::integer
                                    FROM npc_dialogue_texts predecessor
                                    JOIN npc_dialogue_texts current_text
                                      ON current_text.npc_key = predecessor.npc_key
                                     AND current_text.scene_key = predecessor.scene_key
                                     AND current_text.display_name = predecessor.display_name
                                     AND current_text.description = predecessor.description
                                    WHERE predecessor.revision = @v14Revision
                                      AND current_text.revision = @v15Revision
                                ),
                                (
                                    SELECT COUNT(*)::integer
                                    FROM npc_dialogue_profiles predecessor
                                    JOIN npc_dialogue_profiles current_profile
                                      ON current_profile.profile_key = predecessor.profile_key
                                     AND current_profile.dialog_index = predecessor.dialog_index
                                     AND current_profile.behavior = predecessor.behavior
                                     AND current_profile.initial_request_sub_id =
                                             predecessor.initial_request_sub_id
                                    WHERE predecessor.revision = @v14Revision
                                      AND current_profile.revision = @v15Revision
                                ),
                                (
                                    SELECT COUNT(*)::integer
                                    FROM npc_dialogue_profile_entries predecessor
                                    JOIN npc_dialogue_profile_entries current_entry
                                      ON current_entry.profile_key = predecessor.profile_key
                                     AND current_entry.menu_order = predecessor.menu_order
                                     AND current_entry.sub_id = predecessor.sub_id
                                    WHERE predecessor.revision = @v14Revision
                                      AND current_entry.revision = @v15Revision
                                ),
                                (
                                    SELECT COUNT(*)::integer
                                    FROM npc_dialogue_bindings predecessor
                                    JOIN npc_dialogue_bindings current_binding
                                      ON current_binding.npc_key = predecessor.npc_key
                                     AND current_binding.route_order = predecessor.route_order
                                     AND current_binding.client_script_key =
                                             predecessor.client_script_key
                                     AND current_binding.profile_key = predecessor.profile_key
                                    WHERE predecessor.revision = @v14Revision
                                      AND current_binding.revision = @v15Revision
                                )
                         FROM npc_dialogue_revisions release
                         WHERE release.revision = @v15Revision;
                         """))
        {
            AddV14AndV15Revisions(command);
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync(), "canonical V15 release exists");
            Check.True(
                reader.GetString(0) ==
                    NpcDialogueBaselineV15.ExpectedSpawnRevision &&
                reader.GetInt32(1) == NpcDialogueBaselineV15.ExpectedTextCount &&
                reader.GetInt32(2) ==
                    NpcDialogueBaselineV15.ExpectedProfileCount &&
                reader.GetInt32(3) == NpcDialogueBaselineV15.ExpectedRouteCount &&
                reader.GetInt32(4) ==
                    NpcDialogueBaselineV15.ExpectedMenuEntryCount &&
                reader.GetString(5) == NpcDialogueBaselineV15.Source,
                "V15 declares its canonical V2-spawn dependency, counts, " +
                "and source");
            Check.True(
                reader.GetInt32(6) == NpcDialogueBaselineV14.ExpectedTextCount &&
                reader.GetInt32(7) ==
                    NpcDialogueBaselineV14.ExpectedProfileCount &&
                reader.GetInt32(8) ==
                    NpcDialogueBaselineV14.ExpectedMenuEntryCount &&
                reader.GetInt32(9) == NpcDialogueBaselineV14.ExpectedRouteCount,
                "V15 preserves every V14 text and geometry row exactly");
        }

        await AssertV15ArenaGeometryAsync(dataSource);
    }

    private static async Task AssertV15ArenaGeometryAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT binding.npc_key,
                   binding.client_script_key,
                   binding.profile_key,
                   binding.route_order,
                   profile.dialog_index,
                   profile.behavior,
                   profile.initial_request_sub_id,
                   ARRAY_AGG(entry.sub_id ORDER BY entry.menu_order),
                   text.scene_key,
                   LENGTH(text.display_name) > 0,
                   LENGTH(text.description) > 0
            FROM npc_dialogue_bindings binding
            JOIN npc_dialogue_profiles profile
              ON profile.revision = binding.revision
             AND profile.profile_key = binding.profile_key
            JOIN npc_dialogue_profile_entries entry
              ON entry.revision = profile.revision
             AND entry.profile_key = profile.profile_key
            JOIN npc_dialogue_texts text
              ON text.revision = binding.revision
             AND text.npc_key = binding.npc_key
            WHERE binding.revision = @v15Revision
              AND binding.npc_key IN ('Arena_002', 'Arena_003')
            GROUP BY binding.npc_key,
                     binding.client_script_key,
                     binding.profile_key,
                     binding.route_order,
                     profile.dialog_index,
                     profile.behavior,
                     profile.initial_request_sub_id,
                     text.scene_key,
                     text.display_name,
                     text.description
            ORDER BY binding.npc_key;
            """);
        command.Parameters.AddWithValue(
            "v15Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV15.ExpectedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        var expectedKeys = new[]
        {
            DuelArenaTransporterProtocol.DoorkeeperNpcKey,
            DuelArenaTransporterProtocol.GatekeeperNpcKey
        };
        var rowIndex = 0;
        while (await reader.ReadAsync())
        {
            Check.True(
                rowIndex < expectedKeys.Length,
                "only reviewed V15 Arena endpoints are present");
            var npcKey = expectedKeys[rowIndex++];
            Check.True(
                reader.GetString(0) == npcKey &&
                reader.GetString(1) == npcKey &&
                reader.GetString(2) == "duel_arena_transporter" &&
                reader.GetInt16(3) == 0 &&
                reader.GetInt32(4) ==
                    DuelArenaTransporterProtocol.DialogIndex &&
                reader.GetInt16(5) ==
                    (short)NpcDialogueBehavior.DuelArenaTransporter &&
                reader.GetInt32(6) ==
                    DuelArenaTransporterProtocol.InitialRequestSubId &&
                reader.GetFieldValue<int[]>(7).SequenceEqual(
                    DuelArenaTransporterProtocol.InitialMenuSubIds) &&
                reader.GetString(8) == "Arena" &&
                reader.GetBoolean(9) &&
                reader.GetBoolean(10),
                $"V15 publishes native text and the bounded route for {npcKey}");
        }

        Check.Equal(2, rowIndex, "V15 adds exactly two Duel Arena bindings");
    }

    private static void AddV14AndV15Revisions(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue(
            "v14Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV14.ExpectedRevision);
        command.Parameters.AddWithValue(
            "v15Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV15.ExpectedRevision);
    }
}
