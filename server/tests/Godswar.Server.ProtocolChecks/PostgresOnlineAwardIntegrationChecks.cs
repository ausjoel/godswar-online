using Godswar.Server.Infrastructure.Pets;
using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.OnlineAwards;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOnlineAwardIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Online Award claims and management";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b09|b12)_[a-z0-9_]{1,48}$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            throw new CheckSkippedException($"{CheckName} requires a disposable B09/B12 database; " +
                $"received '{database}'");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var items = await PostgresItemTemplateContentBootstrapper.LoadAsync(
            connectionString);
        var pets = await PostgresPetContentBootstrapper.LoadAsync(connectionString, items);
        var balance = await new PostgresOnlineAwardBalanceSnapshotReader(
            dataSource,
            items, pets).ReadAsync();
        AssertBaseline(balance);
        await AssertFoundationEvidenceAsync(dataSource, balance);

        var fixture = await CreateOnlineAwardFixtureAsync(
            dataSource,
            "commit");
        var calendar = RealmCalendar.CreateForTesting(
            new RealmId(fixture.RealmId),
            "Asia/Manila");
        var executor = new PostgresOnlineAwardCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            balance,
            calendar,
            items, pets);
        var historical = await AssertCommitReplayAndDailyFenceAsync(
            dataSource,
            executor,
            fixture,
            calendar,
            balance);
        await AssertBagFullRollbackAsync(
            dataSource,
            executor,
            calendar);
        await AssertConcurrentClaimAsync(
            dataSource,
            executor,
            calendar);
        await AssertRealmDayBoundaryAsync(
            dataSource,
            executor,
            calendar);
        var currentBalance = await AssertManagementAsync(
            dataSource,
            items,
            pets,
            balance);
        var restartedExecutor = new PostgresOnlineAwardCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            currentBalance,
            calendar,
            items, pets);
        var historicalReplay = await restartedExecutor.ExecuteAsync(
            historical.Envelope);
        Check.True(
            historicalReplay.Disposition ==
                OnlineAwardExecutionDisposition.Duplicate &&
            HasSameReceipt(historicalReplay.Receipt, historical.Receipt),
            "a restarted worker replays a receipt from a historical balance");
    }

    private static void AssertBaseline(OnlineAwardBalanceSnapshot balance)
    {
        Check.True(
            balance.Revision == 1 &&
            balance.Sha256 ==
                "A11516DCF5A5CAC3AAAC9CECEFF416C6510789F4386BBAF4C8D7CC9FB2B704FE" &&
            balance.Rewards.SequenceEqual(BaselineRewards()),
            "startup pins the exact reviewed Online Award balance");
    }

    private static OnlineAwardRewardEntry[] BaselineRewards() =>
    [
        new(0, 10150, 1, 14, 0, 1),
        new(1, 10150, 4, 10, 0, 1),
        new(2, 10134, 5, 1, 0, 99),
        new(3, 11005, 5, 1, 0, 99)
    ];

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        return Convert.ToString(await command.ExecuteScalarAsync()) ??
            string.Empty;
    }
}
