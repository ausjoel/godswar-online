using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialoguePublicationIntegrationChecks
{
    private static async Task AssertTransporterRoutesAsync(
        NpgsqlDataSource dataSource,
        IWorldContentReader worldContent)
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
                $"{endpoint.NpcKey} stores its endpoint-specific route");

            var dialogue = await worldContent.ReadNpcDialogueAsync(
                endpoint.NpcKey);
            Check.True(
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].ClientScriptKey == endpoint.NpcKey &&
                dialogue.Routes[0].RouteOrder == 0 &&
                dialogue.Routes[0].DialogIndex ==
                    TransporterProtocol.DialogIndex &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.Transporter &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    endpoint.Menu),
                $"{endpoint.NpcKey} loader pins its finite Transporter menu");
        }

        Check.Equal(3, rowIndex, "three ordinary Transporter routes publish");
    }

    private static async Task AssertEndpointProfileSchemaAsync(
        NpgsqlDataSource dataSource)
    {
        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT pg_get_constraintdef(oid)
                         FROM pg_constraint
                         WHERE conrelid =
                                   'public.npc_dialogue_profiles'::regclass
                           AND conname =
                                   'ck_npc_dialogue_profiles_behavior';
                         """))
        {
            var definition = (string?)await command.ExecuteScalarAsync();
            Check.True(
                definition is not null &&
                definition.Contains("behavior <= 17", StringComparison.Ordinal),
                "dialogue profile behavior constraint admits Arena services 17");
        }

        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT COUNT(*)::integer
                         FROM pg_constraint
                         WHERE conrelid =
                                   'public.npc_dialogue_profiles'::regclass
                           AND conname =
                                   'uq_npc_dialogue_profiles_behavior';
                         """))
        {
            Check.Equal(
                0,
                (int)(await command.ExecuteScalarAsync() ?? -1),
                "profile behavior is no longer globally unique per revision");
        }

        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT to_regclass(
                             'public.ux_npc_dialogue_profiles_non_transporter_behavior'
                         ) IS NULL,
                         to_regclass(
                             'public.ux_npc_dialogue_profiles_singleton_behavior'
                         ) IS NOT NULL;
                         """))
        {
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync() &&
                reader.GetBoolean(0) &&
                reader.GetBoolean(1),
                "singleton behaviors retain per-revision uniqueness");
        }

        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT profile.behavior,
                                COUNT(DISTINCT profile.profile_key)::integer
                         FROM npc_dialogue_publication publication
                         JOIN npc_dialogue_profiles profile
                           ON profile.revision = publication.revision
                         WHERE publication.family = 'npc-dialogues'
                           AND profile.behavior IN (14, 15, 16, 17)
                         GROUP BY profile.behavior
                         ORDER BY profile.behavior;
                         """))
        {
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync() &&
                reader.GetInt16(0) == 14 &&
                reader.GetInt32(1) == 3 &&
                await reader.ReadAsync() &&
                reader.GetInt16(0) == 15 &&
                reader.GetInt32(1) == 2 &&
                await reader.ReadAsync() &&
                reader.GetInt16(0) == 16 &&
                reader.GetInt32(1) == 2 &&
                await reader.ReadAsync() &&
                reader.GetInt16(0) == 17 &&
                reader.GetInt32(1) == 3 &&
                !await reader.ReadAsync(),
                "transporters and captured Arena services retain their " +
                "endpoint-specific profiles");
        }
    }
}
