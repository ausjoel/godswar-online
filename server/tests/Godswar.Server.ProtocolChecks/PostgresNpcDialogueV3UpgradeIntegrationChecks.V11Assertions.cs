using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private static async Task AssertV11DeltaAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT release.text_count,
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
                       WHERE predecessor.revision = @v10Revision
                         AND current_text.revision = @v11Revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_profiles predecessor
                       JOIN npc_dialogue_profiles current_profile
                         ON current_profile.profile_key =
                                predecessor.profile_key
                        AND current_profile.dialog_index =
                                predecessor.dialog_index
                        AND current_profile.behavior = predecessor.behavior
                        AND current_profile.initial_request_sub_id =
                                predecessor.initial_request_sub_id
                       WHERE predecessor.revision = @v10Revision
                         AND current_profile.revision = @v11Revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_profile_entries predecessor
                       JOIN npc_dialogue_profile_entries current_entry
                         ON current_entry.profile_key = predecessor.profile_key
                        AND current_entry.menu_order = predecessor.menu_order
                        AND current_entry.sub_id = predecessor.sub_id
                       WHERE predecessor.revision = @v10Revision
                         AND current_entry.revision = @v11Revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_bindings predecessor
                       JOIN npc_dialogue_bindings current_binding
                         ON current_binding.npc_key = predecessor.npc_key
                        AND current_binding.route_order =
                                predecessor.route_order
                        AND current_binding.client_script_key =
                                predecessor.client_script_key
                        AND current_binding.profile_key =
                                predecessor.profile_key
                       WHERE predecessor.revision = @v10Revision
                         AND current_binding.revision = @v11Revision
                   )
            FROM npc_dialogue_revisions release
            WHERE release.revision = @v11Revision;
            """);
        command.Parameters.AddWithValue(
            "v10Revision", NpcDialogueBaselineV10.ExpectedRevision);
        command.Parameters.AddWithValue(
            "v11Revision", NpcDialogueBaselineV11.ExpectedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "canonical V11 release exists");
        Check.True(
            reader.GetInt32(0) == NpcDialogueBaselineV11.ExpectedTextCount &&
            reader.GetInt32(1) == NpcDialogueBaselineV11.ExpectedProfileCount &&
            reader.GetInt32(2) == NpcDialogueBaselineV11.ExpectedRouteCount &&
            reader.GetInt32(3) ==
                NpcDialogueBaselineV11.ExpectedMenuEntryCount &&
            reader.GetString(4) == NpcDialogueBaselineV11.Source,
            "V11 declares its canonical release counts and source");
        Check.True(
            reader.GetInt32(5) == NpcDialogueBaselineV10.ExpectedTextCount &&
            reader.GetInt32(6) ==
                NpcDialogueBaselineV10.ExpectedProfileCount &&
            reader.GetInt32(7) ==
                NpcDialogueBaselineV10.ExpectedMenuEntryCount &&
            reader.GetInt32(8) ==
                NpcDialogueBaselineV10.ExpectedRouteCount,
            "V11 preserves every V10 text, profile, menu, and route row");
    }

    private static async Task AssertTransporterRoutesAsync(
        NpgsqlDataSource dataSource)
    {
        var expected = new[]
        {
            (NpcKey: "Athens_041", ProfileKey: "athens_transporter",
                Menu: TransporterProtocol.AthensInitialMenuSubIds),
            (NpcKey: "Mycenae_All_013", ProfileKey: "mycenae_transporter",
                Menu: TransporterProtocol.MycenaeInitialMenuSubIds),
            (NpcKey: "Sparta_042", ProfileKey: "sparta_transporter",
                Menu: TransporterProtocol.SpartaInitialMenuSubIds)
        };
        await using var command = dataSource.CreateCommand(
            """
            SELECT binding.npc_key,
                   binding.client_script_key,
                   binding.profile_key,
                   binding.route_order,
                   profile.dialog_index,
                   profile.behavior,
                   profile.initial_request_sub_id,
                   ARRAY_AGG(entry.sub_id ORDER BY entry.menu_order)
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_bindings binding
              ON binding.revision = publication.revision
            JOIN npc_dialogue_profiles profile
              ON profile.revision = binding.revision
             AND profile.profile_key = binding.profile_key
            JOIN npc_dialogue_profile_entries entry
              ON entry.revision = profile.revision
             AND entry.profile_key = profile.profile_key
            WHERE publication.family = 'npc-dialogues'
              AND binding.npc_key IN (
                  'Athens_041', 'Mycenae_All_013', 'Sparta_042')
            GROUP BY binding.npc_key,
                     binding.client_script_key,
                     binding.profile_key,
                     binding.route_order,
                     profile.dialog_index,
                     profile.behavior,
                     profile.initial_request_sub_id
            ORDER BY binding.npc_key;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var rowIndex = 0;
        while (await reader.ReadAsync())
        {
            Check.True(rowIndex < expected.Length,
                "only reviewed Transporter bindings are published");
            var endpoint = expected[rowIndex++];
            Check.True(
                reader.GetString(0) == endpoint.NpcKey &&
                reader.GetString(1) == endpoint.NpcKey &&
                reader.GetString(2) == endpoint.ProfileKey &&
                reader.GetInt16(3) == 0 &&
                reader.GetInt32(4) == TransporterProtocol.DialogIndex &&
                reader.GetInt16(5) ==
                    (short)NpcDialogueBehavior.Transporter &&
                reader.GetInt32(6) ==
                    TransporterProtocol.InitialRequestSubId &&
                reader.GetFieldValue<int[]>(7).SequenceEqual(endpoint.Menu),
                $"{endpoint.NpcKey} adds its endpoint-specific V11 route");
        }

        Check.Equal(3, rowIndex, "V11 adds three ordinary Transporter routes");
    }

    private static async Task AssertTransporterProfileSchemaAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT pg_get_constraintdef(behavior_check.oid),
                   NOT EXISTS (
                       SELECT 1
                       FROM pg_constraint uniqueness
                       WHERE uniqueness.conrelid =
                                 'public.npc_dialogue_profiles'::regclass
                         AND uniqueness.conname =
                                 'uq_npc_dialogue_profiles_behavior'),
                   to_regclass(
                       'public.ux_npc_dialogue_profiles_non_transporter_behavior'
                   ) IS NOT NULL,
                   (
                       SELECT COUNT(DISTINCT profile.profile_key)::integer
                       FROM npc_dialogue_profiles profile
                       WHERE profile.revision = @v11Revision
                         AND profile.behavior = 14
                   )
            FROM pg_constraint behavior_check
            WHERE behavior_check.conrelid =
                      'public.npc_dialogue_profiles'::regclass
              AND behavior_check.conname =
                      'ck_npc_dialogue_profiles_behavior';
            """);
        command.Parameters.AddWithValue(
            "v11Revision", NpcDialogueBaselineV11.ExpectedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "dialogue profile behavior constraint exists");
        Check.True(
            reader.GetString(0).Contains(
                "behavior <= 14", StringComparison.Ordinal) &&
            reader.GetBoolean(1) &&
            reader.GetBoolean(2) &&
            reader.GetInt32(3) == 3,
            "schema shares behavior 14 while earlier behaviors stay unique");
    }
}
