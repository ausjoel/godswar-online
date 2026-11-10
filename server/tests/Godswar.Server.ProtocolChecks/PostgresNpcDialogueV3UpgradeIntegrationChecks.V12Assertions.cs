using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private static async Task AssertV14DeltaAsync(
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
                         AND current_text.revision = @v14Revision
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
                       WHERE predecessor.revision = @v10Revision
                         AND current_profile.revision = @v14Revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_profile_entries predecessor
                       JOIN npc_dialogue_profile_entries current_entry
                         ON current_entry.profile_key = predecessor.profile_key
                        AND current_entry.menu_order = predecessor.menu_order
                        AND current_entry.sub_id = predecessor.sub_id
                       WHERE predecessor.revision = @v10Revision
                         AND current_entry.revision = @v14Revision
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
                       WHERE predecessor.revision = @v10Revision
                         AND current_binding.revision = @v14Revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_texts predecessor
                       JOIN npc_dialogue_texts current_text
                         ON current_text.npc_key = predecessor.npc_key
                        AND current_text.scene_key = predecessor.scene_key
                        AND current_text.display_name = predecessor.display_name
                        AND current_text.description = predecessor.description
                       WHERE predecessor.revision = @v13Revision
                         AND current_text.revision = @v14Revision
                   )
            FROM npc_dialogue_revisions release
            WHERE release.revision = @v14Revision;
            """);
        command.Parameters.AddWithValue(
            "v10Revision", NpcDialogueBaselineV10.ExpectedRevision);
        command.Parameters.AddWithValue(
            "v13Revision", NpcDialogueBaselineV13.ExpectedRevision);
        command.Parameters.AddWithValue(
            "v14Revision", NpcDialogueBaselineV14.ExpectedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "canonical V14 release exists");
        Check.True(
            reader.GetInt32(0) == NpcDialogueBaselineV14.ExpectedTextCount &&
            reader.GetInt32(1) == NpcDialogueBaselineV14.ExpectedProfileCount &&
            reader.GetInt32(2) == NpcDialogueBaselineV14.ExpectedRouteCount &&
            reader.GetInt32(3) ==
                NpcDialogueBaselineV14.ExpectedMenuEntryCount &&
            reader.GetString(4) == NpcDialogueBaselineV14.Source,
            "V14 declares its canonical release counts and source");
        Check.True(
            reader.GetInt32(5) ==
                NpcDialogueBaselineV10.ExpectedTextCount - 4 &&
            reader.GetInt32(6) ==
                NpcDialogueBaselineV10.ExpectedProfileCount &&
            reader.GetInt32(7) ==
                NpcDialogueBaselineV10.ExpectedMenuEntryCount &&
            reader.GetInt32(8) ==
                NpcDialogueBaselineV10.ExpectedRouteCount &&
            reader.GetInt32(9) ==
                NpcDialogueBaselineV13.ExpectedTextCount - 2,
            "V14 replaces only two Battlefield and two Instance Caller " +
            "texts relative to V10, changes only two Instance Caller texts " +
            "relative to V13, and preserves every V10 geometry row");
    }

    private static async Task AssertV14BattlefieldRoutesAsync(
        NpgsqlDataSource dataSource)
    {
        var expected = new[]
        {
            (NpcKey: "Athens_056",
                ProfileKey: "athens_battlefield_transporter",
                Menu: BattlefieldTransporterProtocol.AthensInitialMenuSubIds),
            (NpcKey: "Sparta_056",
                ProfileKey: "sparta_battlefield_transporter",
                Menu: BattlefieldTransporterProtocol.SpartaInitialMenuSubIds)
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
              AND binding.npc_key IN ('Athens_056', 'Sparta_056')
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
                "only reviewed V14 Battlefield routes are published");
            var endpoint = expected[rowIndex++];
            Check.True(
                reader.GetString(0) == endpoint.NpcKey &&
                reader.GetString(1) == endpoint.NpcKey &&
                reader.GetString(2) == endpoint.ProfileKey &&
                reader.GetInt16(3) == 0 &&
                reader.GetInt32(4) ==
                    BattlefieldTransporterProtocol.DialogIndex &&
                reader.GetInt16(5) ==
                    (short)NpcDialogueBehavior.BattlefieldTransporter &&
                reader.GetInt32(6) ==
                    BattlefieldTransporterProtocol.InitialRequestSubId &&
                reader.GetFieldValue<int[]>(7).SequenceEqual(endpoint.Menu),
                $"{endpoint.NpcKey} retains its endpoint-specific route");
        }

        Check.Equal(2, rowIndex, "V14 retains two Battlefield routes");
    }

    private static async Task AssertCurrentProfileSchemaAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT pg_get_constraintdef(behavior_check.oid),
                   to_regclass(
                       'public.ux_npc_dialogue_profiles_non_transporter_behavior'
                   ) IS NULL,
                   to_regclass(
                       'public.ux_npc_dialogue_profiles_singleton_behavior'
                   ) IS NOT NULL,
                   (
                       SELECT COUNT(DISTINCT profile.profile_key)::integer
                       FROM npc_dialogue_profiles profile
                       WHERE profile.revision = @v14Revision
                         AND profile.behavior = 14
                   ),
                   (
                       SELECT COUNT(DISTINCT profile.profile_key)::integer
                       FROM npc_dialogue_profiles profile
                       WHERE profile.revision = @v14Revision
                         AND profile.behavior = 15
                   )
            FROM pg_constraint behavior_check
            WHERE behavior_check.conrelid =
                      'public.npc_dialogue_profiles'::regclass
              AND behavior_check.conname =
                      'ck_npc_dialogue_profiles_behavior';
            """);
        command.Parameters.AddWithValue(
            "v14Revision", NpcDialogueBaselineV14.ExpectedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "dialogue profile behavior constraint exists");
        Check.True(
            reader.GetString(0).Contains(
                "behavior <= 17", StringComparison.Ordinal) &&
            reader.GetBoolean(1) &&
            reader.GetBoolean(2) &&
            reader.GetInt32(3) == 3 &&
            reader.GetInt32(4) == 2,
            "V21 retains Duel Arena while inherited transporter behaviors " +
            "retain their reviewed sharing rules");

        await using var currentCommand = dataSource.CreateCommand(
            """
            SELECT COUNT(*)::integer
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_profiles profile
              ON profile.revision = publication.revision
            WHERE publication.family = 'npc-dialogues'
              AND profile.behavior = 16;
            """);
        Check.Equal(
            2,
            (int)(await currentCommand.ExecuteScalarAsync() ?? -1),
            "V21 publishes separate captured Gatekeeper and Ward profiles");
        await AssertBehaviorEighteenRejectedAsync(dataSource);
    }

    private static async Task AssertBehaviorEighteenRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        const string revision =
            "1818181818181818181818181818181818181818181818181818181818181818";
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO npc_dialogue_revisions (
                    revision, spawn_revision, text_count, profile_count,
                    route_count, menu_entry_count, source)
                VALUES (
                    @revision, @spawnRevision, 1, 1, 1, 1,
                    'behavior-18-rejection-fixture');

                INSERT INTO npc_dialogue_profiles (
                    revision, profile_key, dialog_index, behavior,
                    initial_request_sub_id)
                VALUES (@revision, 'behavior_18', 1, 18, -1);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue(
                "spawnRevision",
                NpcDialogueBaselineV21.ExpectedSpawnRevision);
            _ = await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex)
        {
            Check.True(
                ex.SqlState == PostgresErrorCodes.CheckViolation &&
                ex.ConstraintName == "ck_npc_dialogue_profiles_behavior",
                "behavior 18 is rejected by the finite dialogue-behavior " +
                "constraint");
            await transaction.RollbackAsync();
            return;
        }

        await transaction.RollbackAsync();
        throw new InvalidOperationException(
            "Dialogue behavior 18 bypassed the reviewed finite domain.");
    }
}
