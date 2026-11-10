using System.Text.Json;
using System.Text.RegularExpressions;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresLegacyInstanceOpalPaymentIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL compensated Atlantis Opal retries";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        "^godswar_(?:legacy_instance_opal_[a-f0-9]{8}|" +
        "b(?:03|12)_[a-z0-9_]{8,48})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var databaseName = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(databaseName))
        {
            throw new CheckSkippedException($"{CheckName} requires a disposable database; " +
                $"received '{databaseName}'");
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();
        await using (var gameStore = new PostgresGameStore(connectionString))
        {
            await gameStore.EnsureSeedDataAsync();
        }

        var catalog = await PostgresItemTemplateContentBootstrapper
            .LoadAsync(dataSource);
        await AssertOfficialOpalPublicationAsync(dataSource, catalog);

        var store = new PostgresLegacyInstanceOpalPaymentStore(
            dataSource,
            new PostgresOutboxDispatcherOptions());
        await AssertAtomicChargeAndReplayAsync(dataSource, store);
        await AssertSettlementCompensationAsync(dataSource, store);
        await AssertMixedOwnershipRecoveryFenceAsync(dataSource, store);
        await AssertScopedRecoveryAsync(dataSource, store);
        await AssertCrashAndInvariantHardeningAsync(dataSource, store);
    }

    private static async Task AssertOfficialOpalPublicationAsync(
        NpgsqlDataSource dataSource,
        PinnedItemTemplateCatalog catalog)
    {
        Check.True(
            catalog.Revision.Source.Contains(
                "+opal-v1",
                StringComparison.Ordinal),
            "official item publication identifies its reviewed Opal source");
        Check.True(
            catalog.TryGet(
                LegacyInstanceOpalPaymentPolicy.OpalItemTemplateId,
                out var opal),
            "the immutable pinned catalog contains Opal 3932");
        Check.True(
            opal.Id == 3932 &&
            opal.Kind == "consume item" &&
            opal.NameKey == "Earphone3932" &&
            opal.DisplayName == "Opal" &&
            opal.EquipmentSlot == 0 &&
            opal.ClassIds.Count == 0 &&
            opal.MinLevel is null &&
            opal.MaxLevel is null &&
            opal.Hand is null &&
            opal.SkillFlag is null &&
            opal.Texture ==
                "./Localization/en_us/UI/Texture/Icon.gwo" &&
            opal.Icon == "540,900",
            "Opal 3932 retains its exact reviewed client identity");

        using var stats = JsonDocument.Parse(opal.StatsJson);
        var root = stats.RootElement;
        Check.True(
            root.GetProperty("ID").GetString() == "3932" &&
            root.GetProperty("Type").GetString() == "consume item" &&
            root.GetProperty("Texture").GetString() == opal.Texture &&
            root.GetProperty("Icon").GetString() == opal.Icon &&
            root.GetProperty("Random").GetString() == "0" &&
            root.GetProperty("Distribution").GetString() == "0,0" &&
            root.GetProperty("Money").GetString() == "0" &&
            root.GetProperty("Overlap").GetString() == "99" &&
            root.EnumerateObject().Count() == 8,
            "Opal 3932 has the exact immutable stock properties");

        await ExpectThrowsAsync<PostgresException>(async () =>
        {
            await using var command = dataSource.CreateCommand(
                "UPDATE public.item_template_content_definitions " +
                "SET display_name = 'Mutable Opal' " +
                "WHERE revision = @revision AND id = 3932;");
            command.Parameters.AddWithValue(
                "revision",
                catalog.Revision.Sha256);
            _ = await command.ExecuteNonQueryAsync();
        }, "sealed Opal publication rejects mutation");

    }

    private static async Task AssertAtomicChargeAndReplayAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceOpalPaymentStore store)
    {
        var stacked = await CreatePayerAsync(dataSource);
        var singleton = await CreatePayerAsync(dataSource);
        var missing = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(dataSource, stacked.CharacterId, 2, 3);
        await InsertOpalAsync(dataSource, singleton.CharacterId, 4, 1);

        var failedReservation = Guid.NewGuid();
        await InsertDailyClaimAsync(
            dataSource,
            failedReservation,
            RealmId.Tempest,
            stacked.CharacterId);
        await InsertDailyClaimAsync(
            dataSource,
            failedReservation,
            RealmId.Tempest,
            missing.CharacterId);
        var failed = await store.ChargeAsync(Request(
            failedReservation,
            RealmId.Tempest,
            [stacked, missing],
            Utc(1, 0)));
        Check.True(
            failed.Status ==
                LegacyInstanceOpalChargeStatus.InsufficientOpal &&
            failed.FailedCharacterIds.SetEquals([missing.CharacterId]) &&
            failed.Mutations.Count == 0,
            "one missing party Opal rejects the complete charge");
        Check.Equal(
            3,
            await ReadOpalQuantityAsync(dataSource, stacked.CharacterId),
            "failed party charge consumes no available Opal");
        Check.Equal(
            0L,
            await ReadInventoryRevisionAsync(
                dataSource,
                stacked.CharacterId),
            "failed party charge advances no inventory revision");
        Check.Equal(
            0,
            await CountPaymentRowsAsync(dataSource, failedReservation),
            "failed party charge persists no partial payment rows");
        await AssertInventoryEvidenceAsync(
            dataSource,
            stacked.CharacterId,
            expectedRevision: 0,
            expectedEvidenceRows: 0,
            expectedChargeRows: 0,
            expectedRefundRows: 0);
        await AssertInventoryEvidenceAsync(
            dataSource,
            missing.CharacterId,
            expectedRevision: 0,
            expectedEvidenceRows: 0,
            expectedChargeRows: 0,
            expectedRefundRows: 0);

        var reservation = Guid.NewGuid();
        await InsertDailyClaimAsync(
            dataSource,
            reservation,
            RealmId.Tempest,
            stacked.CharacterId);
        await InsertDailyClaimAsync(
            dataSource,
            reservation,
            RealmId.Tempest,
            singleton.CharacterId);
        var request = Request(
            reservation,
            RealmId.Tempest,
            [stacked, singleton],
            Utc(1, 5));
        var charged = await store.ChargeAsync(request);
        Check.True(
            charged.Status == LegacyInstanceOpalChargeStatus.Charged &&
            charged.FailedCharacterIds.Count == 0 &&
            charged.Mutations.Count == 2,
            "stacked and singleton Opals charge atomically");
        Check.Equal(
            2,
            await ReadOpalQuantityAsync(dataSource, stacked.CharacterId),
            "stacked Opal charge decrements exactly one");
        Check.Equal(
            0,
            await ReadOpalQuantityAsync(dataSource, singleton.CharacterId),
            "singleton Opal charge removes its stack");
        await AssertInventoryEvidenceAsync(
            dataSource,
            stacked.CharacterId,
            expectedRevision: 1,
            expectedEvidenceRows: 1,
            expectedChargeRows: 1,
            expectedRefundRows: 0);
        await AssertInventoryEvidenceAsync(
            dataSource,
            singleton.CharacterId,
            expectedRevision: 1,
            expectedEvidenceRows: 1,
            expectedChargeRows: 1,
            expectedRefundRows: 0);
        Check.Equal(
            1,
            await CountItemAuditRowsAsync(
                dataSource,
                singleton.CharacterId,
                "delete"),
            "singleton charge leaves deletion audit evidence");

        var replay = await store.ChargeAsync(request);
        Check.True(
            replay.Status == LegacyInstanceOpalChargeStatus.Charged &&
            replay.Mutations.SequenceEqual(charged.Mutations),
            "pending charge replay returns the original mutations");
        Check.Equal(
            1L,
            await ReadInventoryRevisionAsync(
                dataSource,
                stacked.CharacterId),
            "pending replay does not charge twice");

        var commit = await store.SettleAsync(
            reservation,
            [stacked.CharacterId, singleton.CharacterId]);
        Check.Equal(
            0,
            commit.RefundMutations.Count,
            "admitted party settlement commits without refunds");
        Check.True(
            (await ReadPaymentStatusesAsync(dataSource, reservation))
                .Values.All(static status => status == "committed"),
            "admitted party payments are durably committed");
        Check.Equal(
            0,
            (await store.SettleAsync(
                reservation,
                [stacked.CharacterId, singleton.CharacterId]))
                .RefundMutations.Count,
            "commit replay is idempotent");
        await ExpectThrowsAsync<InvalidOperationException>(
            () => store.ChargeAsync(request),
            "terminal payment cannot replay as a fresh charge");
    }

    private static LegacyInstanceOpalChargeRequest Request(
        Guid reservationId,
        RealmId realmId,
        IReadOnlyCollection<OpalPayerFixture> payers,
        DateTimeOffset chargedAtUtc) => new(
        reservationId,
        realmId,
        payers.Select(static payer => payer.ToPayer()).ToArray(),
        chargedAtUtc);

    private static DateTimeOffset Utc(int hour, int minute) =>
        new(2026, 9, 1, hour, minute, 0, TimeSpan.Zero);

    private static async Task ExpectThrowsAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }
}
