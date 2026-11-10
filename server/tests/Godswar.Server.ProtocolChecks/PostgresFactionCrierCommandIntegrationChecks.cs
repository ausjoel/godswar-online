using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.FactionCrier;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Faction Crier transactions";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly DateTimeOffset ReviewedMondayInstant =
        new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly RealmCalendar TestRealmCalendar =
        RealmCalendar.CreateForTesting(
            RealmId.Tempest,
            "Asia/Manila");

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
        var itemContent = await PostgresItemTemplateContentBootstrapper
            .LoadAsync(connectionString);
        var balance = await PostgresFactionCrierBalanceSnapshotReader
            .LoadAsync(dataSource);
        var executor = new PostgresFactionCrierCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            balance,
            TestRealmCalendar,
            itemContent.Revision.Sha256);

        await AssertRealmCalendarFenceAsync(
            dataSource,
            executor,
            balance,
            itemContent.Revision.Sha256);
        await AssertDailyCommitReplayAndClaimFenceAsync(
            dataSource,
            executor,
            balance);
        await AssertWeeklyGoldCommitAsync(dataSource, executor, balance);
        await AssertRenewalAndStaleSelectionAsync(
            dataSource,
            executor,
            balance);
        await AssertPaidTurnInsAsync(dataSource, executor, balance);
        await AssertMaxLevelExperienceTurnInAsync(
            dataSource,
            executor,
            balance);
        await AssertTerminalRollbackAsync(dataSource, executor);
    }

    private static CommandEnvelope<FactionCrierCommand> CreateEnvelope(
        CrierFixture fixture,
        FactionCrierOperationIdentity identity,
        int subId,
        int realmPeriodDayNumber,
        FactionCrierNameplateSelection? renewalSource = null,
        DateTimeOffset? receivedAt = null)
    {
        if (!FactionCrierCommandEnvelope.TryCreateCommand(
                identity,
                fixture.RealmId,
                FactionCrierCommandEnvelope.AthensNpcId,
                FactionCrierCommandEnvelope.DialogIndex,
                subId,
                realmPeriodDayNumber,
                renewalSource,
                out var command))
        {
            throw new InvalidOperationException(
                "The Faction Crier fixture requested an invalid command.");
        }

        return PlayerOwnershipTestFences.Bind(
            FactionCrierCommandEnvelope.Create(
                fixture.Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                receivedAt ?? ReviewedMondayInstant,
                command));
    }

    private static FactionCrierExecutionReceipt RequireReceipt(
        FactionCrierExecutionResult result,
        FactionCrierExecutionDisposition disposition,
        string description)
    {
        Check.Equal(
            (int)disposition,
            (int)result.Disposition,
            $"{description} disposition");
        return result.Receipt ?? throw new InvalidOperationException(
            $"{description} returned no durable receipt.");
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        return Convert.ToString(await command.ExecuteScalarAsync()) ??
            string.Empty;
    }
}
