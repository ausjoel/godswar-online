using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private static async Task AssertV16DeltaAsync(
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
                                      AND current_text.revision = @v16Revision
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
                                      AND current_profile.revision = @v16Revision
                                ),
                                (
                                    SELECT COUNT(*)::integer
                                    FROM npc_dialogue_profile_entries predecessor
                                    JOIN npc_dialogue_profile_entries current_entry
                                      ON current_entry.profile_key = predecessor.profile_key
                                     AND current_entry.menu_order = predecessor.menu_order
                                     AND current_entry.sub_id = predecessor.sub_id
                                    WHERE predecessor.revision = @v14Revision
                                      AND current_entry.revision = @v16Revision
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
                                      AND current_binding.revision = @v16Revision
                                )
                         FROM npc_dialogue_revisions release
                         WHERE release.revision = @v16Revision;
                         """))
        {
            AddV14AndV16Revisions(command);
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync(), "canonical V16 release exists");
            Check.True(
                reader.GetString(0) ==
                    NpcDialogueBaselineV16.ExpectedSpawnRevision &&
                reader.GetInt32(1) == NpcDialogueBaselineV16.ExpectedTextCount &&
                reader.GetInt32(2) ==
                    NpcDialogueBaselineV16.ExpectedProfileCount &&
                reader.GetInt32(3) == NpcDialogueBaselineV16.ExpectedRouteCount &&
                reader.GetInt32(4) ==
                    NpcDialogueBaselineV16.ExpectedMenuEntryCount &&
                reader.GetString(5) == NpcDialogueBaselineV16.Source,
                "V16 declares its canonical V3-spawn dependency, counts, " +
                "and source");
            Check.True(
                reader.GetInt32(6) == NpcDialogueBaselineV14.ExpectedTextCount &&
                reader.GetInt32(7) ==
                    NpcDialogueBaselineV14.ExpectedProfileCount &&
                reader.GetInt32(8) ==
                    NpcDialogueBaselineV14.ExpectedMenuEntryCount &&
                reader.GetInt32(9) == NpcDialogueBaselineV14.ExpectedRouteCount,
                "V16 preserves every V14 text and geometry row exactly");
            Check.True(
                NpcDialogueBaselineV16.ExpectedTextCount ==
                    NpcDialogueBaselineV14.ExpectedTextCount + 2 &&
                NpcDialogueBaselineV16.ExpectedProfileCount ==
                    NpcDialogueBaselineV14.ExpectedProfileCount + 1 &&
                NpcDialogueBaselineV16.ExpectedRouteCount ==
                    NpcDialogueBaselineV14.ExpectedRouteCount + 2 &&
                NpcDialogueBaselineV16.ExpectedMenuEntryCount ==
                    NpcDialogueBaselineV14.ExpectedMenuEntryCount + 1,
                "V16 adds only the paired Duel Arena text, profile, route, " +
                "and menu rows to V14");
        }

        await AssertV16ArenaBindingsAndDescriptionsAsync(dataSource);
    }

    private static async Task AssertV16ArenaBindingsAndDescriptionsAsync(
        NpgsqlDataSource dataSource)
    {
        var expected = new[]
        {
            (NpcKey: DuelArenaTransporterProtocol.DoorkeeperNpcKey,
                Description: NpcDialogueBaselineV16.DoorkeeperDescription),
            (NpcKey: DuelArenaTransporterProtocol.GatekeeperNpcKey,
                Description: NpcDialogueBaselineV16.GatekeeperDescription)
        };
        Check.True(
            !string.Equals(
                expected[0].Description,
                expected[1].Description,
                StringComparison.Ordinal),
            "V16 uses endpoint-specific Duel Arena direction descriptions");

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
                   text.description
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
            WHERE binding.revision = @v16Revision
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
            "v16Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV16.ExpectedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        var rowIndex = 0;
        while (await reader.ReadAsync())
        {
            Check.True(
                rowIndex < expected.Length,
                "only the two reviewed V16 Arena endpoints are present");
            var endpoint = expected[rowIndex++];
            Check.True(
                reader.GetString(0) == endpoint.NpcKey &&
                reader.GetString(1) == endpoint.NpcKey &&
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
                reader.GetString(10) == endpoint.Description,
                $"V16 publishes the bounded route and direction text for " +
                endpoint.NpcKey);
        }

        Check.Equal(
            expected.Length,
            rowIndex,
            "V16 publishes exactly two Duel Arena bindings");
    }

    private static void AddV14AndV16Revisions(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue(
            "v14Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV14.ExpectedRevision);
        command.Parameters.AddWithValue(
            "v16Revision",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV16.ExpectedRevision);
    }
}
