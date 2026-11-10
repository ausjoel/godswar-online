using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    public const string PetMergeRankCheckName =
        "PostgreSQL durable pet Merge-rank evidence";

    public static async Task RunPetMergeRankOnlyAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{PetMergeRankCheckName} " +
                $"({ConnectionStringVariable} is not set)");
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            throw new CheckSkippedException($"{PetMergeRankCheckName} requires a disposable " +
                $"B03/B12 database; received '{database}'");
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();
        GameplayItemContent itemContent;
        IPetContentCatalog petContent;
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            itemContent = store.ItemContent;
            petContent = store.PetContent;
        }

        var ownerMergeContent =
            await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(
                dataSource);
        var options = new PostgresOutboxDispatcherOptions();
        var executor = new PostgresPetDurableCommandExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMergeContent,
            PetLearnedSkillContentBaseline.Create(),
            new FixedPetHatchRankRollSource(89));
        var restarted = new PostgresPetDurableCommandExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMergeContent,
            PetLearnedSkillContentBaseline.Create(),
            new ThrowingPetHatchRankRollSource());
        await AssertPetToPetMergeAsync(
            connectionString,
            dataSource,
            executor,
            restarted,
            itemContent);
    }
}
