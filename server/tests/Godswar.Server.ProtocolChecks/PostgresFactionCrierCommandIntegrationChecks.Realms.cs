using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.FactionCrier;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    private static async Task AssertRealmCalendarFenceAsync(
        NpgsqlDataSource dataSource,
        PostgresFactionCrierCommandExecutor tempestExecutor,
        FactionCrierBalanceSnapshot balance,
        string itemContentRevision)
    {
        var fixture = await CreateFixtureAsync(
            dataSource,
            "realmfence",
            silver: 0,
            gold: 0,
            bindingGold: 0);
        Check.Equal(
            RealmId.Tempest.Value,
            fixture.RealmId,
            "realm-fence fixture is a Tempest character");
        var dwargonCalendar = RealmCalendar.CreateForTesting(
            RealmId.Dwargon,
            "Asia/Manila");
        var dwargonExecutor = new PostgresFactionCrierCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            balance,
            dwargonCalendar,
            itemContentRevision);
        var dwargonFixture = fixture with
        {
            RealmId = RealmId.Dwargon.Value
        };
        var identity = FactionCrierOperationIdentity.SecureClient(
            Guid.NewGuid());
        var day = dwargonCalendar.GetDay(ReviewedMondayInstant);
        var envelope = CreateEnvelope(
            dwargonFixture,
            identity,
            subId: 1,
            realmPeriodDayNumber: day.DayNumber);

        Check.Equal(
            (int)FactionCrierExecutionDisposition.InvalidIntent,
            (int)(await tempestExecutor.ExecuteAsync(envelope)).Disposition,
            "the command realm must match the selected calendar realm");
        Check.Equal(
            (int)FactionCrierExecutionDisposition.PreconditionFailed,
            (int)(await dwargonExecutor.ExecuteAsync(envelope)).Disposition,
            "a Dwargon calendar cannot mutate a Tempest character");
        Check.Equal(
            (int)FactionCrierExecutionDisposition.PreconditionFailed,
            (int)(await dwargonExecutor.TryReplayAsync(
                fixture.Subject,
                fixture.Ownership,
                new FactionCrierReplayIntent(RealmId.Dwargon.Value, 1),
                identity)).Disposition,
            "Faction Crier replay proves the character realm before inbox read");

        var state = await ReadStateAsync(dataSource, fixture);
        Check.True(
            state.InboxCount == 0 &&
            state.AuditCount == 0 &&
            state.OutboxCount == 0 &&
            state.DailyCount == 0 &&
            state.InventoryRevision == 0 &&
            state.FactionCrierRevision == 0,
            "cross-realm command and replay attempts leave no durable evidence");
    }
}
