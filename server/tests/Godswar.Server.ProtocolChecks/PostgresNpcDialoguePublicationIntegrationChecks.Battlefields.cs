using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialoguePublicationIntegrationChecks
{
    private static async Task AssertBattlefieldRoutesAsync(
        NpgsqlDataSource dataSource,
        IWorldContentReader worldContent)
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
                "only reviewed Battlefield bindings are published");
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
                $"{endpoint.NpcKey} stores its Battlefield route");

            var dialogue = await worldContent.ReadNpcDialogueAsync(
                endpoint.NpcKey);
            Check.True(
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.BattlefieldTransporter &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    endpoint.Menu),
                $"{endpoint.NpcKey} loader pins its Battlefield menu");
        }

        Check.Equal(2, rowIndex, "two Battlefield Transporter routes publish");
    }

    private static async Task AssertInstanceCallerRoutesAsync(
        NpgsqlDataSource dataSource,
        IWorldContentReader worldContent)
    {
        foreach (var npcKey in new[] { "Athens_060", "Sparta_060" })
        {
            await using var command = dataSource.CreateCommand(
                """
                SELECT ARRAY_AGG(entry.sub_id ORDER BY entry.menu_order)
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
                  AND binding.npc_key = @npcKey
                  AND profile.behavior = 11;
                """);
            command.Parameters.AddWithValue("npcKey", npcKey);
            var stored = (int[]?)await command.ExecuteScalarAsync();
            var dialogue = await worldContent.ReadNpcDialogueAsync(npcKey);
            Check.True(
                stored is not null &&
                stored.SequenceEqual(InstanceCallerProtocol.InitialMenuSubIds) &&
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    InstanceCallerProtocol.InitialMenuSubIds),
                $"{npcKey} stores Medusa, Atlantis, and Wonderland");
        }
    }
}
