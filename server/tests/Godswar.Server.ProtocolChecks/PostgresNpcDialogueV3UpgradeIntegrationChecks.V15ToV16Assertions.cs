using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private static async Task AssertV15ToV16DeltaAsync(
        NpgsqlDataSource dataSource)
    {
        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT
                           (SELECT COUNT(*)::integer
                            FROM npc_dialogue_texts predecessor
                            JOIN npc_dialogue_texts current_text
                              ON current_text.npc_key = predecessor.npc_key
                             AND current_text.scene_key = predecessor.scene_key
                             AND current_text.display_name = predecessor.display_name
                             AND current_text.description = predecessor.description
                            WHERE predecessor.revision = @v15Revision
                              AND current_text.revision = @v16Revision),
                           (SELECT COUNT(*)::integer
                            FROM npc_dialogue_profiles predecessor
                            JOIN npc_dialogue_profiles current_profile
                              ON current_profile.profile_key = predecessor.profile_key
                             AND current_profile.dialog_index = predecessor.dialog_index
                             AND current_profile.behavior = predecessor.behavior
                             AND current_profile.initial_request_sub_id =
                                     predecessor.initial_request_sub_id
                            WHERE predecessor.revision = @v15Revision
                              AND current_profile.revision = @v16Revision),
                           (SELECT COUNT(*)::integer
                            FROM npc_dialogue_profile_entries predecessor
                            JOIN npc_dialogue_profile_entries current_entry
                              ON current_entry.profile_key = predecessor.profile_key
                             AND current_entry.menu_order = predecessor.menu_order
                             AND current_entry.sub_id = predecessor.sub_id
                            WHERE predecessor.revision = @v15Revision
                              AND current_entry.revision = @v16Revision),
                           (SELECT COUNT(*)::integer
                            FROM npc_dialogue_bindings predecessor
                            JOIN npc_dialogue_bindings current_binding
                              ON current_binding.npc_key = predecessor.npc_key
                             AND current_binding.route_order = predecessor.route_order
                             AND current_binding.client_script_key =
                                     predecessor.client_script_key
                             AND current_binding.profile_key = predecessor.profile_key
                            WHERE predecessor.revision = @v15Revision
                              AND current_binding.revision = @v16Revision);
                         """))
        {
            AddV15AndV16Revisions(command);
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync() &&
                reader.GetInt32(0) ==
                    NpcDialogueBaselineV15.ExpectedTextCount - 2 &&
                reader.GetInt32(1) ==
                    NpcDialogueBaselineV15.ExpectedProfileCount &&
                reader.GetInt32(2) ==
                    NpcDialogueBaselineV15.ExpectedMenuEntryCount &&
                reader.GetInt32(3) ==
                    NpcDialogueBaselineV15.ExpectedRouteCount,
                "V16 changes only the two Arena descriptions from V15");
        }

        var expected = new[]
        {
            (DuelArenaTransporterProtocol.DoorkeeperNpcKey,
                NpcDialogueBaselineV16.DoorkeeperDescription),
            (DuelArenaTransporterProtocol.GatekeeperNpcKey,
                NpcDialogueBaselineV16.GatekeeperDescription)
        };
        await using var changed = dataSource.CreateCommand(
            """
            SELECT predecessor.npc_key,
                   predecessor.scene_key = current_text.scene_key,
                   predecessor.display_name = current_text.display_name,
                   predecessor.description,
                   current_text.description
            FROM npc_dialogue_texts predecessor
            JOIN npc_dialogue_texts current_text
              ON current_text.npc_key = predecessor.npc_key
            WHERE predecessor.revision = @v15Revision
              AND current_text.revision = @v16Revision
              AND ROW(predecessor.scene_key,
                      predecessor.display_name,
                      predecessor.description)
                  IS DISTINCT FROM
                  ROW(current_text.scene_key,
                      current_text.display_name,
                      current_text.description)
            ORDER BY predecessor.npc_key COLLATE "C";
            """);
        AddV15AndV16Revisions(changed);
        await using var changedReader = await changed.ExecuteReaderAsync();
        var index = 0;
        while (await changedReader.ReadAsync())
        {
            Check.True(index < expected.Length, "V16 has no extra text delta");
            var endpoint = expected[index++];
            Check.True(
                changedReader.GetString(0) == endpoint.Item1 &&
                changedReader.GetBoolean(1) &&
                changedReader.GetBoolean(2) &&
                !string.Equals(
                    changedReader.GetString(3),
                    changedReader.GetString(4),
                    StringComparison.Ordinal) &&
                changedReader.GetString(4) == endpoint.Item2,
                $"V16 changes only {endpoint.Item1}'s direction text");
        }
        Check.Equal(2, index, "V16 changes both Arena direction descriptions");
    }

    private static void AddV15AndV16Revisions(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue(
            "v15Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV15.ExpectedRevision);
        command.Parameters.AddWithValue(
            "v16Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV16.ExpectedRevision);
    }
}
