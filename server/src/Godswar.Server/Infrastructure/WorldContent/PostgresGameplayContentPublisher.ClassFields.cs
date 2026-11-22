using Godswar.Server.Application.Combat;
using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

// B22 B23-F (the admin d86): the class field skills (Earthsplitter 100-104, Storm of Spears 360-364, Holy Pyre 870-874) and
// their 15 books. Their rows are authored in the mutable source tables skill_templates / skill_book_templates with
// stats->>'Generated' = 'b23-class-field' (Development/batch-2026-11-22/B23-F/sql). At start-up this step (after the
// G2 Aether Sanctum step) builds the successor of the current publication = every predecessor row without the marker
// plus the marked source rows, hashes it and, when that differs, stores and publishes it (the K1 skill-level pattern).
// Removing the source rows (undo SQL) republishes the predecessor (its rows still exist, only the pointer moves).
internal static partial class PostgresGameplayContentPublisher
{
    private const string ClassFieldPublisher = "server-database-class-fields-v1";
    internal const string ClassFieldMarker = "b23-class-field";

    private static bool IsClassFieldRow(string statsJson) =>
        statsJson.Contains("\"Generated\": \"" + ClassFieldMarker + "\"", StringComparison.Ordinal);

    private static async Task<GameplayContentPublicationResult>
        EnsureClassFieldExtensionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            GameplayContentPublicationResult current,
            CancellationToken cancellationToken)
    {
        var predecessor = await PostgresWorldContentReaderLoader
            .LoadPublishedGameplayContentAsync(connection, transaction, cancellationToken);
        var sourceSkills = (await ReadSkillsAsync(connection, transaction, cancellationToken))
            .Where(static skill => IsClassFieldRow(skill.StatsJson))
            .ToArray();
        var sourceBooks = (await ReadSkillBooksAsync(connection, transaction, cancellationToken))
            .Where(static book => IsClassFieldRow(book.StatsJson))
            .ToArray();
        var baseSkills = predecessor.SkillCombatDefinitions
            .Where(static skill => !IsClassFieldRow(skill.StatsJson))
            .ToArray();
        var baseBooks = predecessor.SkillBooks
            .Where(static book => !IsClassFieldRow(book.StatsJson))
            .ToArray();
        if (sourceSkills.Any(skill => !ClassFieldSkills.IsField(skill.SkillId) ||
                baseSkills.Any(existing => existing.SkillId == skill.SkillId)) ||
            sourceBooks.Any(book => baseBooks.Any(existing => existing.ItemId == book.ItemId)) ||
            sourceBooks.Any(book => sourceSkills.All(skill => skill.SkillId != book.SkillId)))
        {
            throw new WorldContentUnavailableException(
                "gameplay",
                WorldContentFailureReason.RevisionMismatch,
                "Generated class-field skills collide with published rows, use an id outside 100-104 / 360-364 / " +
                "870-874, or a book names a skill that is not generated.");
        }

        var candidate = predecessor with
        {
            SkillCombatDefinitions = baseSkills.Concat(sourceSkills)
                .OrderBy(static skill => skill.SkillId)
                .ToArray(),
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
        if (string.Equals(revision.Sha256, current.Revision, StringComparison.Ordinal))
        {
            return current;
        }

        if (await InsertReleaseAsync(connection, transaction, revision, successor, cancellationToken))
        {
            await CopySkillLevelSuccessorAsync(
                connection,
                transaction,
                current.Revision,
                revision.Sha256,
                successor,
                cancellationToken,
                ClassFieldMarker);
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
            command.Parameters.AddWithValue("publisher", NpgsqlDbType.Varchar, ClassFieldPublisher);
            command.Parameters.AddWithValue("predecessor_revision", NpgsqlDbType.Varchar, current.Revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new WorldContentUnavailableException(
                    "gameplay",
                    WorldContentFailureReason.RevisionMismatch,
                    "The gameplay publication pointer changed during the class-field publication.");
            }
        }

        Console.WriteLine(
            $"[gameplay-content] B23-F class fields {(sourceSkills.Length > 0 ? "added" : "removed")}: " +
            $"{current.Revision[..8]} -> {revision.Sha256[..8]} ({sourceSkills.Length} skills, {sourceBooks.Length} books)");
        return new GameplayContentPublicationResult(
            revision.Sha256,
            revision.EntryCount,
            Source,
            Created: true,
            Publisher: ClassFieldPublisher);
    }
}
