using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialoguePublicationIntegrationChecks
{
    private static async Task AssertDuelArenaRoutesAsync(
        NpgsqlDataSource dataSource,
        IWorldContentReader worldContent)
    {
        var expectedEndpoints = new[]
        {
            (NpcKey: "Arena_003", ScriptKey: "Arena_002", DialogIndex: 87,
                ProfileKey: NpcDialogueBaselineV20.GatekeeperProfileKey),
            (NpcKey: "Arena_004", ScriptKey: "Arena_004", DialogIndex: 88,
                ProfileKey: NpcDialogueBaselineV20.WardProfileKey)
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
              AND binding.npc_key IN ('Arena_002', 'Arena_003', 'Arena_004')
              AND profile.behavior = 16
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
            Check.True(
                rowIndex < expectedEndpoints.Length,
                "only the two reviewed Duel Arena bindings are published");
            var endpoint = expectedEndpoints[rowIndex++];
            Check.True(
                reader.GetString(0) == endpoint.NpcKey &&
                reader.GetString(1) == endpoint.ScriptKey &&
                reader.GetString(2) == endpoint.ProfileKey &&
                reader.GetInt16(3) == 0 &&
                reader.GetInt32(4) ==
                    endpoint.DialogIndex &&
                reader.GetInt16(5) ==
                    (short)NpcDialogueBehavior.DuelArenaTransporter &&
                reader.GetInt32(6) ==
                    -1 &&
                reader.GetFieldValue<int[]>(7).SequenceEqual([-1]),
                $"{endpoint.NpcKey} stores the captured initial-action route");
        }

        Check.Equal(
            expectedEndpoints.Length,
            rowIndex,
            "both Duel Arena transport endpoints publish");
        foreach (var endpoint in expectedEndpoints)
        {
            var dialogue = await worldContent.ReadNpcDialogueAsync(endpoint.NpcKey);
            Check.True(
                !string.IsNullOrWhiteSpace(dialogue.Text.DisplayName) &&
                !string.IsNullOrWhiteSpace(dialogue.Text.Description) &&
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].ClientScriptKey == endpoint.ScriptKey &&
                dialogue.Routes[0].RouteOrder == 0 &&
                dialogue.Routes[0].DialogIndex ==
                    endpoint.DialogIndex &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.DuelArenaTransporter &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual([-1]),
                $"{endpoint.NpcKey} loader pins its captured route and native text");
        }

        var supportActors = new[]
        {
            (DuelArenaNpcRoster.VendorNpcKey, "Arena Vendor"),
            ("DuelArena_001", "[Warehouse] Akou")
        };
        foreach (var (npcKey, displayName) in supportActors)
        {
            var dialogue = await worldContent.ReadNpcDialogueAsync(npcKey);
            Check.True(
                dialogue.Text.DisplayName == displayName &&
                !string.IsNullOrWhiteSpace(dialogue.Text.Description) &&
                dialogue.Routes.Count == 0,
                $"{npcKey} publishes its stock text without an invented " +
                "service route");
        }
        await AssertDuelArenaServiceRoutesAsync(worldContent);
    }

    internal static async Task AssertDuelArenaServiceRoutesAsync(
        IWorldContentReader worldContent)
    {
        foreach (var endpoint in new[]
                 {
                     (NpcKey: "Arena_002", Dialog: 88, Menu: new[] { -1 }),
                     (NpcKey: "Arena_005", Dialog: 32, Menu: new[] { 1, 100, 101, 102 }),
                     (NpcKey: "Arena_006", Dialog: 95, Menu: new[] { -1 })
                 })
        {
            var dialogue = await worldContent.ReadNpcDialogueAsync(endpoint.NpcKey);
            Check.True(dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].RouteOrder == 0 &&
                dialogue.Routes[0].ClientScriptKey == endpoint.NpcKey &&
                dialogue.Routes[0].Behavior == NpcDialogueBehavior.DuelArenaServices &&
                dialogue.Routes[0].DialogIndex == endpoint.Dialog &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(endpoint.Menu),
                $"{endpoint.NpcKey} pins its captured service endpoint");
        }
    }
}
