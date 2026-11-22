using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

// B22 (item 13, the admin d42): player skill levels 6-10 (ids family*10 + 5..9) and their books.
// The rows are authored in the mutable source tables skill_templates / skill_book_templates with
// stats->>'Generated' = 'b22-k1-skill-levels' (Development/batch-2026-11-22/K1/sql). At start-up this step
// builds the successor of the current publication = every non-generated row of the predecessor plus the
// generated source rows, hashes it and, when that differs from the current revision, stores and publishes it.
// Removing the source rows (undo SQL) republishes the predecessor (its rows still exist, only the pointer moves).
internal static partial class PostgresGameplayContentPublisher
{
    private const string SkillLevelExtensionPublisher =
        "server-database-skill-levels-v1";
    internal const string SkillLevelExtensionMarker = "b22-k1-skill-levels";

    private static bool IsSkillLevelExtension(string statsJson) =>
        statsJson.Contains(
            "\"Generated\": \"" + SkillLevelExtensionMarker + "\"",
            StringComparison.Ordinal);

    private static async Task<GameplayContentPublicationResult>
        EnsureSkillLevelExtensionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            GameplayContentPublicationResult current,
            CancellationToken cancellationToken)
    {
        var predecessor = await PostgresWorldContentReaderLoader
            .LoadPublishedGameplayContentAsync(
                connection,
                transaction,
                cancellationToken);
        var sourceSkills = (await ReadSkillsAsync(
                connection,
                transaction,
                cancellationToken))
            .Where(static skill => IsSkillLevelExtension(skill.StatsJson))
            .ToArray();
        var sourceBooks = (await ReadSkillBooksAsync(
                connection,
                transaction,
                cancellationToken))
            .Where(static book => IsSkillLevelExtension(book.StatsJson))
            .ToArray();
        var baseSkills = predecessor.SkillCombatDefinitions
            .Where(static skill => !IsSkillLevelExtension(skill.StatsJson))
            .ToArray();
        var baseBooks = predecessor.SkillBooks
            .Where(static book => !IsSkillLevelExtension(book.StatsJson))
            .ToArray();
        if (sourceSkills.Any(skill => skill.SkillId % 10 < 5 ||
                baseSkills.Any(existing => existing.SkillId == skill.SkillId)) ||
            sourceBooks.Any(book => baseBooks.Any(existing =>
                existing.ItemId == book.ItemId)) ||
            sourceBooks.Any(book => sourceSkills.All(skill =>
                skill.SkillId != book.SkillId)))
        {
            throw new WorldContentUnavailableException(
                "gameplay",
                WorldContentFailureReason.RevisionMismatch,
                "Generated skill levels 6-10 collide with published rows " +
                "or name a skill that is not generated.");
        }

        var candidate = predecessor with
        {
            SkillCombatDefinitions = baseSkills.Concat(sourceSkills)
                .OrderBy(static skill => skill.SkillId)
                .ToArray()
        };
        candidate = candidate with
        {
            SkillBooks = baseBooks.Concat(sourceBooks)
                .OrderBy(static book => book.ItemId)
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
        if (string.Equals(
                revision.Sha256,
                current.Revision,
                StringComparison.Ordinal))
        {
            return current;
        }

        if (await InsertReleaseAsync(
                connection,
                transaction,
                revision,
                successor,
                cancellationToken))
        {
            await CopySkillLevelSuccessorAsync(
                connection,
                transaction,
                current.Revision,
                revision.Sha256,
                successor,
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
            command.Parameters.AddWithValue(
                "publisher",
                NpgsqlDbType.Varchar,
                SkillLevelExtensionPublisher);
            command.Parameters.AddWithValue(
                "predecessor_revision",
                NpgsqlDbType.Varchar,
                current.Revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new WorldContentUnavailableException(
                    "gameplay",
                    WorldContentFailureReason.RevisionMismatch,
                    "The gameplay publication pointer changed during the " +
                    "skill-level publication.");
            }
        }

        return new GameplayContentPublicationResult(
            revision.Sha256,
            revision.EntryCount,
            Source,
            Created: true,
            Publisher: SkillLevelExtensionPublisher);
    }

    private static async Task CopySkillLevelSuccessorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predecessorRevision,
        string revision,
        GameplayContentCatalog content,
        CancellationToken cancellationToken,
        string generatedMarker = SkillLevelExtensionMarker) // B22 B23-F: also the class-field rows
    {
        var marker = "stats->>'Generated' = '" + generatedMarker + "'";
        var notMarker = "(stats->>'Generated') IS DISTINCT FROM '" + generatedMarker + "'";
        var copies = new (string Description, int Count, string Sql)[]
        {
            ("maps", content.Maps.Count,
                """
                INSERT INTO gameplay_map_definitions (
                    revision, map_id, scene_key, display_name, client_scene_id, map_mode)
                SELECT @revision, map_id, scene_key, display_name, client_scene_id, map_mode
                FROM gameplay_map_definitions WHERE revision = @predecessor_revision;
                """),
            ("address points", content.AddressPoints.Count,
                """
                INSERT INTO gameplay_map_address_points (
                    revision, map_id, group_index, point_index, group_name, name, pos_x, pos_z, source)
                SELECT @revision, map_id, group_index, point_index, group_name, name, pos_x, pos_z, source
                FROM gameplay_map_address_points WHERE revision = @predecessor_revision;
                """),
            ("map links", content.Links.Count,
                """
                INSERT INTO gameplay_map_links (
                    revision, map_id, link_index, target_map_id, pos_x, pos_z, source, confidence, activation, note)
                SELECT @revision, map_id, link_index, target_map_id, pos_x, pos_z, source, confidence, activation, note
                FROM gameplay_map_links WHERE revision = @predecessor_revision;
                """),
            ("monster templates", content.MonsterTemplates.Count,
                """
                INSERT INTO gameplay_monster_templates (
                    revision, source_key, source_kind, source_map_id, scene_key, template_key, display_name, rank,
                    is_boss, is_elite, is_pet, attack_type, collision_range)
                SELECT @revision, source_key, source_kind, source_map_id, scene_key, template_key, display_name, rank,
                       is_boss, is_elite, is_pet, attack_type, collision_range
                FROM gameplay_monster_templates WHERE revision = @predecessor_revision;
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
                $"""
                INSERT INTO gameplay_skill_combat_definitions (
                    revision, skill_id, target, affect_obj, distance, effect_range, property, mp, power1, power2,
                    cast_time_seconds, cooldown_seconds, display_name, base_name, skill_level, class_ids,
                    previous_skill_id, min_level, max_level, description, stats)
                SELECT @revision, skill_id, target, affect_obj, distance, effect_range, property, mp, power1, power2,
                       cast_time_seconds, cooldown_seconds, display_name, base_name, skill_level, class_ids,
                       previous_skill_id, min_level, max_level, description, stats
                FROM gameplay_skill_combat_definitions
                WHERE revision = @predecessor_revision AND {notMarker}
                UNION ALL
                SELECT @revision, skill_id, target, affect_obj, distance, effect_range, property, mp, power1, power2,
                       intonate_time, cooling_time, display_name, base_name, skill_level, class_ids,
                       previous_skill_id, min_level, max_level, description, stats
                FROM skill_templates WHERE {marker};
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
                $"""
                INSERT INTO gameplay_skill_book_definitions (
                    revision, item_id, name_key, display_name, skill_id, base_name, skill_level, class_ids,
                    min_level, max_level, previous_skill_id, stats)
                SELECT @revision, item_id, name_key, display_name, skill_id, base_name, skill_level, class_ids,
                       min_level, max_level, previous_skill_id, stats
                FROM gameplay_skill_book_definitions
                WHERE revision = @predecessor_revision AND {notMarker}
                UNION ALL
                SELECT @revision, item_id, name_key, display_name, skill_id, base_name, skill_level, class_ids,
                       min_level, max_level, previous_skill_id, stats
                FROM skill_book_templates WHERE {marker};
                """)
        };

        foreach (var copy in copies)
        {
            await using var command = new NpgsqlCommand(copy.Sql, connection, transaction);
            command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, revision);
            command.Parameters.AddWithValue(
                "predecessor_revision",
                NpgsqlDbType.Varchar,
                predecessorRevision);
            var copied = await command.ExecuteNonQueryAsync(cancellationToken);
            if (copied != copy.Count)
            {
                throw new WorldContentUnavailableException(
                    "gameplay",
                    WorldContentFailureReason.RevisionMismatch,
                    $"Skill-level publication copied {copied} {copy.Description}; expected {copy.Count}.");
            }
        }
    }
}
