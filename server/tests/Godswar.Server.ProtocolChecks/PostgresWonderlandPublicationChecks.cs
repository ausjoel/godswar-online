// ROUND3: issue #21 verifies real stage publication using a strictly local copied database.
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresWonderlandPublicationChecks
{
    public const string CheckName = "Wonderland eight-island test instances with copied PostgreSQL monster content";
    public static async Task RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("GODSWAR_WONDERLAND_COPY_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection)) throw new CheckSkippedException("Set a disposable local database copy to run this check.");
        var options = new NpgsqlConnectionStringBuilder(connection);
        if (options.Host != "127.0.0.1" || options.Port < 10000 ||
            options.Database != "wonderland_codex_test")
            throw new InvalidOperationException("Wonderland evidence requires its named loopback-only disposable copy.");
        await using var source = NpgsqlDataSource.Create(options.ConnectionString);
        await using var command = source.CreateCommand("""
            SELECT source_key,source_kind,source_map_id,scene_key,template_key,display_name,rank,
                   is_boss,is_elite,is_pet,attack_type,collision_range
            FROM public.monster_templates WHERE source_map_id=207 ORDER BY source_key
            """);
        var templates = new List<GameplayMonsterTemplateDefinition>();
        await using (var rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync())
                templates.Add(new(rows.GetString(0), rows.GetString(1), rows.GetInt16(2),
                    rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetString(6),
                    rows.GetBoolean(7), rows.GetBoolean(8), rows.GetBoolean(9),
                    rows.IsDBNull(10) ? null : rows.GetInt16(10),
                    rows.IsDBNull(11) ? null : rows.GetFloat(11)));
        Check.True(templates.Count > 0, "the copied database contains published map207 templates");
        var content = GameplayContentCatalog.Empty with { MonsterTemplates = templates.ToArray() };
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            WonderlandMapChecks.CheckFullRun(mode, content);
            Console.WriteLine($"WONDERLAND copy mode={mode} templates={templates.Count} islands=8 actors=115 completed=true");
        }
    }
}
