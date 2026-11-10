using System.Text.RegularExpressions;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Progression;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FighterLevelSealDurabilityChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b09|b12)_[a-z0-9_]{1,48}$",
        RegexOptions.CultureInvariant);

    private static async Task RunPostgresIntegrationAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} PostgreSQL transaction " +
                $"({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = Convert.ToString(await dataSource
            .CreateCommand("SELECT current_database();")
            .ExecuteScalarAsync()) ?? string.Empty;
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            throw new CheckSkippedException($"{CheckName} PostgreSQL transaction requires a " +
                $"disposable B09/B12 database; received '{database}'");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await using var store = new PostgresGameStore(connectionString);
        await AssertCommitReplayAndConflictAsync(
            dataSource,
            store);
        await AssertStaleOwnershipRejectedAsync(
            dataSource,
            store);
    }

    private static async Task AssertCommitReplayAndConflictAsync(
        NpgsqlDataSource dataSource,
        PostgresGameStore store)
    {
        var fixture = await CreateFixtureAsync(
            dataSource,
            "replay",
            bindingGold: 25_000,
            level: 37);
        var sealOperationId = Guid.NewGuid();
        var sealedResult = await store.ChangeFighterLevelSealAsync(
            fixture.AccountId,
            fixture.CharacterId,
            new RealmId(fixture.RealmId),
            fixture.Ownership,
            sealOperationId,
            desiredSealed: true);
        Check.True(
            sealedResult.Status == FighterLevelSealChangeStatus.Sealed &&
            sealedResult.Changed &&
            !sealedResult.Replayed &&
            sealedResult.LevelSealed &&
            sealedResult.BindingGold == 25_000 &&
            sealedResult.SealRevision == 1,
            "an arbitrary-level seal is free and advances seal revision once");

        var replay = await store.ChangeFighterLevelSealAsync(
            fixture.AccountId,
            fixture.CharacterId,
            new RealmId(fixture.RealmId),
            fixture.Ownership,
            sealOperationId,
            desiredSealed: true);
        Check.True(
            replay.Status == FighterLevelSealChangeStatus.Sealed &&
            replay.Replayed &&
            !replay.Changed &&
            replay.LevelSealed &&
            replay.BindingGold == 25_000 &&
            replay.SealRevision == 1 &&
            replay.ReplayProjection is { } sealProjection &&
            sealProjection.LevelSealed &&
            sealProjection.BindingGold == 25_000 &&
            sealProjection.SealRevision == 1,
            "the same seal operation replays with current projection state");

        var conflict = await store.ChangeFighterLevelSealAsync(
            fixture.AccountId,
            fixture.CharacterId,
            new RealmId(fixture.RealmId),
            fixture.Ownership,
            sealOperationId,
            desiredSealed: false);
        Check.True(
            conflict.Status ==
                FighterLevelSealChangeStatus.RequestHashConflict &&
            !conflict.Changed &&
            !conflict.Replayed &&
            conflict.LevelSealed &&
            conflict.BindingGold == 25_000 &&
            conflict.SealRevision == 1,
            "a changed request cannot reuse the seal operation identity");

        var afterSeal = await ReadStateAsync(dataSource, fixture);
        Check.True(
            afterSeal.LevelSealed &&
            afterSeal.BindingGold == 25_000 &&
            afterSeal.WalletRevision == 0 &&
            afterSeal.ProgressionRevision == 0 &&
            afterSeal.SealRevision == 1 &&
            afterSeal.InboxCount == 1 &&
            afterSeal.AuditCount == 1 &&
            afterSeal.CurrencyLedgerCount == 0 &&
            afterSeal.CurrencyDelta == 0 &&
            afterSeal.DuplicateCount == 1 &&
            afterSeal.RequestConflictCount == 1,
            "seal replay and conflict preserve one free durable mutation");

        var unsealOperationId = Guid.NewGuid();
        var unsealedResult = await store.ChangeFighterLevelSealAsync(
            fixture.AccountId,
            fixture.CharacterId,
            new RealmId(fixture.RealmId),
            fixture.Ownership,
            unsealOperationId,
            desiredSealed: false);
        Check.True(
            unsealedResult.Status == FighterLevelSealChangeStatus.Unsealed &&
            unsealedResult.Changed &&
            !unsealedResult.LevelSealed &&
            unsealedResult.BindingGold == 15_000 &&
            unsealedResult.SealRevision == 2,
            "unsealing debits exactly 10,000 Bound Gold");

        var afterUnseal = await ReadStateAsync(dataSource, fixture);
        Check.True(
            !afterUnseal.LevelSealed &&
            afterUnseal.BindingGold == 15_000 &&
            afterUnseal.WalletRevision == 1 &&
            afterUnseal.ProgressionRevision == 0 &&
            afterUnseal.SealRevision == 2 &&
            afterUnseal.InboxCount == 2 &&
            afterUnseal.AuditCount == 2 &&
            afterUnseal.CurrencyLedgerCount == 1 &&
            afterUnseal.CurrencyDelta == -10_000,
            "paid unseal commits one wallet ledger and its own revision");

        var unsealReplay = await store.ChangeFighterLevelSealAsync(
            fixture.AccountId,
            fixture.CharacterId,
            new RealmId(fixture.RealmId),
            fixture.Ownership,
            unsealOperationId,
            desiredSealed: false);
        Check.True(
            unsealReplay.Status == FighterLevelSealChangeStatus.Unsealed &&
            unsealReplay.Replayed &&
            !unsealReplay.Changed &&
            !unsealReplay.LevelSealed &&
            unsealReplay.BindingGold == 15_000 &&
            unsealReplay.SealRevision == 2 &&
            unsealReplay.ReplayProjection is { } unsealProjection &&
            !unsealProjection.LevelSealed &&
            unsealProjection.BindingGold == 15_000 &&
            unsealProjection.SealRevision == 2,
            "a paid-unseal replay carries the current wallet projection");

        var staleSealReplay = await store.ChangeFighterLevelSealAsync(
            fixture.AccountId,
            fixture.CharacterId,
            new RealmId(fixture.RealmId),
            fixture.Ownership,
            sealOperationId,
            desiredSealed: true);
        Check.True(
            staleSealReplay.Status == FighterLevelSealChangeStatus.Sealed &&
            staleSealReplay.Replayed &&
            staleSealReplay.LevelSealed &&
            staleSealReplay.BindingGold == 25_000 &&
            staleSealReplay.SealRevision == 1 &&
            staleSealReplay.ReplayProjection is { } currentProjection &&
            !currentProjection.LevelSealed &&
            currentProjection.BindingGold == 15_000 &&
            currentProjection.SealRevision == 2,
            "an old replay cannot regress a newer Level Sealer projection");

        var afterReplays = await ReadStateAsync(dataSource, fixture);
        Check.True(
            !afterReplays.LevelSealed &&
            afterReplays.BindingGold == 15_000 &&
            afterReplays.WalletRevision == 1 &&
            afterReplays.ProgressionRevision == 0 &&
            afterReplays.SealRevision == 2 &&
            afterReplays.InboxCount == 2 &&
            afterReplays.AuditCount == 2 &&
            afterReplays.CurrencyLedgerCount == 1 &&
            afterReplays.CurrencyDelta == -10_000 &&
            afterReplays.DuplicateCount == 3 &&
            afterReplays.RequestConflictCount == 1,
            "replay projection repair does not repeat durable mutations");
    }

    private static async Task AssertStaleOwnershipRejectedAsync(
        NpgsqlDataSource dataSource,
        PostgresGameStore store)
    {
        var fixture = await CreateFixtureAsync(
            dataSource,
            "stale",
            bindingGold: 25_000,
            level: 103);
        await RotateOwnershipAsync(dataSource, fixture);

        PlayerOwnershipValidationException? rejection = null;
        try
        {
            await store.ChangeFighterLevelSealAsync(
                fixture.AccountId,
                fixture.CharacterId,
                new RealmId(fixture.RealmId),
                fixture.Ownership,
                Guid.NewGuid(),
                desiredSealed: true);
        }
        catch (PlayerOwnershipValidationException ex)
        {
            rejection = ex;
        }
        Check.True(
            rejection?.Status ==
                PlayerOwnershipValidationStatus.OwnershipLost,
            "a stale player owner is rejected before Level Sealer mutation");

        var state = await ReadStateAsync(dataSource, fixture);
        Check.True(
            !state.LevelSealed &&
            state.BindingGold == 25_000 &&
            state.WalletRevision == 0 &&
            state.ProgressionRevision == 0 &&
            state.SealRevision == 0 &&
            state.InboxCount == 0 &&
            state.AuditCount == 0 &&
            state.CurrencyLedgerCount == 0,
            "stale ownership leaves character and evidence unchanged");
    }
}
