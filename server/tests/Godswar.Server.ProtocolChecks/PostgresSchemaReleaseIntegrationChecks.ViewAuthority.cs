using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private sealed record PublicViewSnapshot(
        string Definition,
        string Signature,
        string[] ItemDependencies);

    private static async Task<Dictionary<string, PublicViewSnapshot>> ReadPublicViewDefinitionsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT view_class.relname, pg_get_viewdef(view_class.oid, false),
                (SELECT jsonb_agg(jsonb_build_array(
                    column_definition.attname, column_definition.atttypid,
                    column_definition.atttypmod, column_definition.attcollation)
                    ORDER BY column_definition.attnum)::text
                 FROM pg_attribute column_definition
                 WHERE column_definition.attrelid = view_class.oid
                   AND column_definition.attnum > 0
                   AND NOT column_definition.attisdropped),
                ARRAY(
                    SELECT DISTINCT referenced_relation.relname::text
                    FROM pg_rewrite rewrite
                    JOIN pg_depend dependency
                      ON dependency.classid = 'pg_rewrite'::regclass
                     AND dependency.objid = rewrite.oid
                     AND dependency.refclassid = 'pg_class'::regclass
                    JOIN pg_class referenced_relation
                      ON referenced_relation.oid = dependency.refobjid
                    JOIN pg_namespace referenced_namespace
                      ON referenced_namespace.oid = referenced_relation.relnamespace
                    WHERE rewrite.ev_class = view_class.oid
                      AND referenced_namespace.nspname = 'public'
                      AND referenced_relation.relname IN (
                        'item_templates', 'item_attribute_templates',
                        'official_item_template_content', 'official_item_attribute_content')
                    ORDER BY referenced_relation.relname::text)
            FROM pg_class view_class
            JOIN pg_namespace view_namespace
              ON view_namespace.oid = view_class.relnamespace
            WHERE view_namespace.nspname = 'public' AND view_class.relkind = 'v'
            ORDER BY view_class.relname;
            """, connection);
        var result = new Dictionary<string, PublicViewSnapshot>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0), new(
                reader.GetString(1), reader.GetString(2), reader.GetFieldValue<string[]>(3)));
        }
        return result;
    }

    private static async Task AssertPublicViewDefinitionsAsync(
        string connectionString,
        Dictionary<string, PublicViewSnapshot> migratedViews)
    {
        var seededViews = await ReadPublicViewDefinitionsAsync(connectionString);
        Check.Equal(migratedViews.Count, seededViews.Count,
            "relational content seed bootstrap preserves the migrated public view set");
        foreach (var (name, migrated) in migratedViews)
        {
            Check.True(seededViews.ContainsKey(name),
                $"relational content seed bootstrap preserves public view {name}");
            Check.Equal(migrated.Signature, seededViews[name].Signature,
                $"relational content seed bootstrap preserves column order, names, types, and collations for {name}");
        }

        // pg_dump/restore reparses equivalent literal-array casts in legacy NPC
        // views. Exact SQL preservation belongs to the migrated item views:
        // their definitions must never be replayed from mutable seed assets.
        foreach (var name in new[] { "item_allowed_attributes", "character_equipment_attributes" })
        {
            Check.Equal(migratedViews[name].Definition, seededViews[name].Definition,
                $"relational content seed bootstrap preserves migrated item view {name}");
            AssertOfficialItemViewDependencies(name, migratedViews[name]);
            AssertOfficialItemViewDependencies(name, seededViews[name]);
        }
    }

    private static void AssertOfficialItemViewDependencies(string name, PublicViewSnapshot view)
    {
        Check.True(view.ItemDependencies.SequenceEqual(new[]
            { "official_item_attribute_content", "official_item_template_content" }),
            $"{name} uses both official item publications and no mutable item source tables");
    }
}
