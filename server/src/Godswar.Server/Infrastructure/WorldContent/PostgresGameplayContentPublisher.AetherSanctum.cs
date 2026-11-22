using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

// B22 G2 (the admin d36): the Lv170-200 map Aether Sanctum (map 70, a copy of Olympus). Its rows are authored in the
// mutable source tables map_templates (stats->>'Generated' = 'b22-g2-aether-sanctum'), map_address_points,
// map_links and monster_templates (map_id / source_map_id 70) by Development/batch-2026-11-22/G2/sql. At start-up
// this step builds the successor of the current publication = every predecessor row of other maps plus the
// map-70 source rows and, when its hash differs, stores and publishes it (the K1 skill-level pattern). Removing
// the source rows (undo SQL) republishes the map-70-free content (the old revision's rows still exist).
internal static partial class PostgresGameplayContentPublisher
{
    private const string AetherSanctumPublisher = "server-database-aether-sanctum-v1";
    internal const string AetherSanctumMarker = "b22-g2-aether-sanctum";

    private static async Task<GameplayContentPublicationResult>
        EnsureAetherSanctumExtensionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            GameplayContentPublicationResult current,
            CancellationToken cancellationToken)
    {
        const short mapId = AetherSanctumPolicy.MapId;
        var predecessor = await PostgresWorldContentReaderLoader
            .LoadPublishedGameplayContentAsync(
                connection,
                transaction,
                cancellationToken);
        var sourceMaps = (await ReadMapsAsync(connection, transaction, cancellationToken))
            .Where(static map => map.MapId == mapId)
            .ToArray();
        if (sourceMaps.Length == 1 && !await IsAetherSanctumSourceAsync(connection, transaction, cancellationToken))
        {
            throw new WorldContentUnavailableException(
                "gameplay",
                WorldContentFailureReason.RevisionMismatch,
                "map_templates row 70 is not the generated Aether Sanctum row.");
        }

        var authored = sourceMaps.Length == 1;
        var candidate = predecessor with
        {
            Maps = predecessor.Maps.Where(static map => map.MapId != mapId)
                .Concat(authored ? sourceMaps : [])
                .OrderBy(static map => map.MapId)
                .ToArray(),
            AddressPoints = predecessor.AddressPoints.Where(static point => point.MapId != mapId)
                .Concat(authored
                    ? (await ReadAddressPointsAsync(connection, transaction, cancellationToken))
                        .Where(static point => point.MapId == mapId)
                    : [])
                .OrderBy(static point => point.MapId).ThenBy(static point => point.GroupIndex)
                .ThenBy(static point => point.PointIndex)
                .ToArray(),
            Links = predecessor.Links.Where(static link => link.SourceMapId != mapId)
                .Concat(authored
                    ? (await ReadLinksAsync(connection, transaction, cancellationToken))
                        .Where(static link => link.SourceMapId == mapId)
                    : [])
                .OrderBy(static link => link.SourceMapId).ThenBy(static link => link.LinkIndex)
                .ThenBy(static link => link.TargetMapId)
                .ToArray(),
            MonsterTemplates = predecessor.MonsterTemplates.Where(static template => template.SourceMapId != mapId)
                .Concat(authored
                    ? (await ReadMonsterTemplatesAsync(connection, transaction, cancellationToken))
                        .Where(static template => template.SourceMapId == mapId)
                    : [])
                .OrderBy(static template => template.SourceKey, StringComparer.Ordinal)
                .ThenBy(static template => template.TemplateKey, StringComparer.Ordinal)
                .ToArray()
        };
        var successor = PinnedWorldContentReader.Create(
            "gameplay-publication-validation-v1",
            candidate.Maps.Select(static value => value.MapId),
            [],
            [],
            [],
            gameplay: candidate).Gameplay;
        var revision = WorldContentRevisionHasher.HashGameplay(successor);
        if (string.Equals(revision.Sha256, current.Revision, StringComparison.Ordinal))
        {
            return current;
        }

        if (await InsertReleaseAsync(connection, transaction, revision, successor, cancellationToken))
        {
            await CopyAetherSanctumSuccessorAsync(
                connection,
                transaction,
                current.Revision,
                revision.Sha256,
                successor,
                authored,
                cancellationToken);
        }

        _ = await PostgresWorldContentReaderLoader.LoadGameplayRevisionAsync(
            connection,
            transaction,
            revision.Sha256,
            successor,
            cancellationToken);
        await using (var command = new NpgsqlCommand(
            """
            UPDATE gameplay_content_publication
            SET revision = @revision,
                published_at = now(),
                publisher = @publisher
            WHERE family = 'gameplay'
              AND revision = @predecessor_revision;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, revision.Sha256);
            command.Parameters.AddWithValue("publisher", NpgsqlDbType.Varchar, AetherSanctumPublisher);
            command.Parameters.AddWithValue("predecessor_revision", NpgsqlDbType.Varchar, current.Revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new WorldContentUnavailableException(
                    "gameplay",
                    WorldContentFailureReason.RevisionMismatch,
                    "The gameplay publication pointer changed during the Aether Sanctum publication.");
            }
        }

        Console.WriteLine(
            $"[gameplay-content] B22 G2 Aether Sanctum {(authored ? "added" : "removed")}: " +
            $"{current.Revision[..8]} -> {revision.Sha256[..8]}");
        return new GameplayContentPublicationResult(
            revision.Sha256,
            revision.EntryCount,
            Source,
            Created: true,
            Publisher: AetherSanctumPublisher);
    }

    private static async Task<bool> IsAetherSanctumSourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM map_templates WHERE map_id = @map AND stats->>'Generated' = @marker;",
            connection,
            transaction);
        command.Parameters.AddWithValue("map", NpgsqlDbType.Smallint, (short)AetherSanctumPolicy.MapId);
        command.Parameters.AddWithValue("marker", NpgsqlDbType.Varchar, AetherSanctumMarker);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task CopyAetherSanctumSuccessorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predecessorRevision,
        string revision,
        GameplayContentCatalog content,
        bool authored,
        CancellationToken cancellationToken)
    {
        var source = authored ? "" : " AND false";
        var copies = new (string Description, int Count, string Sql)[]
        {
            ("maps", content.Maps.Count,
                $"""
                INSERT INTO gameplay_map_definitions (
                    revision, map_id, scene_key, display_name, client_scene_id, map_mode)
                SELECT @revision, map_id, scene_key, display_name, client_scene_id, map_mode
                FROM gameplay_map_definitions WHERE revision = @predecessor_revision AND map_id <> 70
                UNION ALL
                SELECT @revision, map_id, scene_key, display_name, client_scene_id, map_mode
                FROM map_templates WHERE map_id = 70{source};
                """),
            ("address points", content.AddressPoints.Count,
                $"""
                INSERT INTO gameplay_map_address_points (
                    revision, map_id, group_index, point_index, group_name, name, pos_x, pos_z, source)
                SELECT @revision, map_id, group_index, point_index, group_name, name, pos_x, pos_z, source
                FROM gameplay_map_address_points WHERE revision = @predecessor_revision AND map_id <> 70
                UNION ALL
                SELECT @revision, map_id, group_index, point_index, group_name, name, pos_x, pos_z, source
                FROM map_address_points WHERE map_id = 70{source};
                """),
            ("map links", content.Links.Count,
                $"""
                INSERT INTO gameplay_map_links (
                    revision, map_id, link_index, target_map_id, pos_x, pos_z, source, confidence, activation, note)
                SELECT @revision, map_id, link_index, target_map_id, pos_x, pos_z, source, confidence, activation, note
                FROM gameplay_map_links WHERE revision = @predecessor_revision AND map_id <> 70
                UNION ALL
                SELECT @revision, map_id, link_index, target_map_id, pos_x, pos_z, source, confidence, activation, note
                FROM map_links WHERE map_id = 70{source};
                """),
            ("monster templates", content.MonsterTemplates.Count,
                $"""
                INSERT INTO gameplay_monster_templates (
                    revision, source_key, source_kind, source_map_id, scene_key, template_key, display_name, rank,
                    is_boss, is_elite, is_pet, attack_type, collision_range)
                SELECT @revision, source_key, source_kind, source_map_id, scene_key, template_key, display_name, rank,
                       is_boss, is_elite, is_pet, attack_type, collision_range
                FROM gameplay_monster_templates
                WHERE revision = @predecessor_revision AND source_map_id IS DISTINCT FROM 70
                UNION ALL
                SELECT @revision, source_key, source_kind, source_map_id, scene_key, template_key, display_name, rank,
                       is_boss, is_elite, is_pet, attack_type, collision_range
                FROM monster_templates WHERE source_map_id = 70{source};
                """),
            ("world bosses", content.WorldBosses.Count,
                """
                INSERT INTO gameplay_world_boss_definitions (
                    revision, map_id, scene_key, template_key, display_name, bonus_basis_points,
                    respawn_interval_seconds)
                SELECT @revision, map_id, scene_key, template_key, display_name, bonus_basis_points,
                       respawn_interval_seconds
                FROM gameplay_world_boss_definitions WHERE revision = @predecessor_revision;
                """),
            ("pending world bosses", content.PendingWorldBossAreas.Count,
                """
                INSERT INTO gameplay_pending_world_boss_areas (revision, map_id, scene_key, reason)
                SELECT @revision, map_id, scene_key, reason
                FROM gameplay_pending_world_boss_areas WHERE revision = @predecessor_revision;
                """),
            ("skills", content.SkillCombatDefinitions.Count,
                """
                INSERT INTO gameplay_skill_combat_definitions (
                    revision, skill_id, target, affect_obj, distance, effect_range, property, mp, power1, power2,
                    cast_time_seconds, cooldown_seconds, display_name, base_name, skill_level, class_ids,
                    previous_skill_id, min_level, max_level, description, stats)
                SELECT @revision, skill_id, target, affect_obj, distance, effect_range, property, mp, power1, power2,
                       cast_time_seconds, cooldown_seconds, display_name, base_name, skill_level, class_ids,
                       previous_skill_id, min_level, max_level, description, stats
                FROM gameplay_skill_combat_definitions WHERE revision = @predecessor_revision;
                """),
            ("classes", content.Classes.Count,
                """
                INSERT INTO gameplay_class_definitions (revision, id, name, display_name, source)
                SELECT @revision, id, name, display_name, source
                FROM gameplay_class_definitions WHERE revision = @predecessor_revision;
                """),
            ("talent effects", content.TalentEffects.Count,
                """
                INSERT INTO gameplay_talent_effect_definitions (revision, id, key, display_name, percent)
                SELECT @revision, id, key, display_name, percent
                FROM gameplay_talent_effect_definitions WHERE revision = @predecessor_revision;
                """),
            ("talents", content.Talents.Count,
                """
                INSERT INTO gameplay_talent_definitions (
                    revision, id, class_id, tree_order, name, prefix_id, required_prefix_rank, required_total_rank,
                    equip_request, effect_type, effect_id, effect_value, is_percent, icon_x, icon_y, icon_width,
                    icon_height, stats)
                SELECT @revision, id, class_id, tree_order, name, prefix_id, required_prefix_rank,
                       required_total_rank, equip_request, effect_type, effect_id, effect_value, is_percent,
                       icon_x, icon_y, icon_width, icon_height, stats
                FROM gameplay_talent_definitions WHERE revision = @predecessor_revision;
                """),
            ("skill books", content.SkillBooks.Count,
                """
                INSERT INTO gameplay_skill_book_definitions (
                    revision, item_id, name_key, display_name, skill_id, base_name, skill_level, class_ids,
                    min_level, max_level, previous_skill_id, stats)
                SELECT @revision, item_id, name_key, display_name, skill_id, base_name, skill_level, class_ids,
                       min_level, max_level, previous_skill_id, stats
                FROM gameplay_skill_book_definitions WHERE revision = @predecessor_revision;
                """)
        };

        foreach (var copy in copies)
        {
            await using var command = new NpgsqlCommand(copy.Sql, connection, transaction);
            command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, revision);
            command.Parameters.AddWithValue("predecessor_revision", NpgsqlDbType.Varchar, predecessorRevision);
            var copied = await command.ExecuteNonQueryAsync(cancellationToken);
            if (copied != copy.Count)
            {
                throw new WorldContentUnavailableException(
                    "gameplay",
                    WorldContentFailureReason.RevisionMismatch,
                    $"Aether Sanctum publication copied {copied} {copy.Description}; expected {copy.Count}.");
            }
        }
    }
}
