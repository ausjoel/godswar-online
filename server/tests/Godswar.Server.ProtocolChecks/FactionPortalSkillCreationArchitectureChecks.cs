using Godswar.Server.Domain.Characters;
namespace Godswar.Server.ProtocolChecks;

internal static class FactionPortalSkillCreationArchitectureChecks
{
    public const string CheckName =
        "Faction portal PostgreSQL character-creation persistence";

    private static readonly string[] CreationPaths =
    [
        "src/Godswar.Server/Infrastructure/Characters/" +
        "PostgresCharacterLifecycleCommandExecutor.Create.cs",
        "src/Godswar.Server/Infrastructure/Characters/" +
        "PostgresFactionPortalSkillWriter.cs"
    ];

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        foreach (var relativePath in CreationPaths)
        {
            CheckCreationPath(root, relativePath);
        }

        var legacyCreation = File.ReadAllText(Path.Combine(root,
            "src/Godswar.Server/State/PostgresGameStore.Characters.Persistence.cs"));
        Check.True(legacyCreation.Contains("PostgresFactionPortalSkillWriter.InsertAsync(",
                StringComparison.Ordinal),
            "compatibility character creation delegates faction portal SQL to its provider writer");

        return Task.CompletedTask;
    }

    private static void CheckCreationPath(
        string root,
        string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var compactSource = string.Concat(
            source.Where(static character => !char.IsWhiteSpace(character)));
        Check.True(
            compactSource.Contains(
                "FactionPortalSkillPolicy.ResolveCapitalPortalSkillId",
                StringComparison.Ordinal),
            $"{relativePath} resolves the character camp through the shared policy");

        const string sourceMarker = "'faction-starter'";
        var markerIndex = source.IndexOf(
            sourceMarker,
            StringComparison.Ordinal);
        var insertIndex = markerIndex < 0
            ? -1
            : source.LastIndexOf(
                "INSERT INTO",
                markerIndex,
                StringComparison.Ordinal);
        var conflictIndex = markerIndex < 0
            ? -1
            : source.IndexOf(
                "ON CONFLICT",
                markerIndex,
                StringComparison.Ordinal);
        Check.True(
            insertIndex >= 0 && conflictIndex > markerIndex,
            $"{relativePath} has a dedicated idempotent faction-starter insert");

        var statement = source[insertIndex..conflictIndex];
        var compact = string.Concat(
                statement.Where(static character =>
                    !char.IsWhiteSpace(character)))
            .Replace("public.", string.Empty, StringComparison.Ordinal);
        Check.True(
            compact.Contains(
                "INSERTINTOcharacter_skills(" +
                "user_id,skill_id,skill_level,source)",
                StringComparison.Ordinal) &&
            compact.Contains(
                "FROMgameplay_skill_combat_definitions",
                StringComparison.Ordinal) &&
            compact.Contains(
                "skill_id=@factionPortalSkillId",
                StringComparison.Ordinal) &&
            compact.Contains(
                "@profession=ANY(",
                StringComparison.Ordinal) &&
            compact.Contains(
                "@gameplayContentRevision",
                StringComparison.Ordinal),
            $"{relativePath} selects the pinned, class-compatible portal template");
        Check.True(
            compact.Contains(
                "SELECT@characterId,@factionPortalSkillId,1," +
                "'faction-starter'",
                StringComparison.Ordinal) ||
            compact.Contains(
                ".skill_id,1,'faction-starter'",
                StringComparison.Ordinal),
            $"{relativePath} persists the faction portal at explicit level one");

        var conflict = source[conflictIndex..];
        Check.True(
            string.Concat(
                    conflict.Take(100).Where(static character =>
                        !char.IsWhiteSpace(character)))
                .StartsWith(
                    "ONCONFLICT(user_id,skill_id)DONOTHING",
                    StringComparison.Ordinal),
            $"{relativePath} cannot duplicate an existing faction portal grant");

        var executionBoundary = string.Concat(
            source[markerIndex..]
                .Take(2_000)
                .Where(static character => !char.IsWhiteSpace(character)));
        Check.True(
            executionBoundary.Contains(
                "ExecuteNonQueryAsync(cancellationToken)!=1",
                StringComparison.Ordinal),
            $"{relativePath} aborts creation unless exactly one portal row is seeded");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "GodswarServer.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root for faction portal checks.");
    }
}
