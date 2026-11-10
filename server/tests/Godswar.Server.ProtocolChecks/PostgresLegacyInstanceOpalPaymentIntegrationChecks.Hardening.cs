using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresLegacyInstanceOpalPaymentIntegrationChecks
{
    private static async Task AssertCrashAndInvariantHardeningAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceOpalPaymentStore store)
    {
        await AssertMixedFreeAndPaidAdmissionsAsync(dataSource, store);
        await AssertMixedFreeStaleRecoveryAsync(dataSource, store);
        await AssertOwnershipHandoffRecoveryAsync(dataSource, store);
        await AssertMalformedRowsRejectedAsync(dataSource, store);
    }

    private static async Task AssertMixedFreeStaleRecoveryAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceOpalPaymentStore paymentStore)
    {
        var freeOffline = await CreatePayerAsync(dataSource);
        var freeTakeover = await CreatePayerAsync(dataSource);
        var paidPending = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(dataSource, paidPending.CharacterId, 34, 1);
        var reservationId = Guid.NewGuid();
        foreach (var member in new[]
                 {
                     freeOffline,
                     freeTakeover,
                     paidPending
                 })
        {
            await InsertDailyClaimAsync(
                dataSource,
                reservationId,
                RealmId.Tempest,
                member.CharacterId);
        }
        Check.True(
            (await paymentStore.ChargeAsync(Request(
                reservationId,
                RealmId.Tempest,
                [paidPending],
                Utc(5, 15)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "mixed stale fixture charges only its returning member");
        await ClearOwnershipAsync(dataSource, freeOffline);
        var takeover = await InstallReplacementOwnershipAsync(
            dataSource,
            freeTakeover);
        var dailyStore = new PostgresLegacyInstanceDailyEntryClaimStore(
            dataSource);
        Check.Equal(
            1,
            await dailyStore.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(6, 0)),
            "background cleanup releases the owner-null free member even " +
            "while another member has a pending payment");
        Check.True(
            !await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                freeOffline.CharacterId) &&
            await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                freeTakeover.CharacterId) &&
            await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                paidPending.CharacterId),
            "background cleanup remains per-player and ownership fenced");
        Check.Equal(
            1,
            await dailyStore.RecoverCharacterAsync(
                RealmId.Tempest,
                freeTakeover.AccountId,
                freeTakeover.CharacterId,
                takeover),
            "replacement owner releases its unpaid crash-stale claim " +
            "before snapshot hydration");
        Check.True(
            !await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                freeTakeover.CharacterId) &&
            await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                paidPending.CharacterId),
            "free login takeover does not disturb another member's " +
            "pending paid claim");
        await ClearOwnershipAsync(dataSource, paidPending);
        Check.Equal(
            1,
            await paymentStore.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(6, 0)),
            "mixed stale fixture later compensates the offline payer");
    }

    private static async Task AssertMixedFreeAndPaidAdmissionsAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceOpalPaymentStore store)
    {
        var freeAdmitted = await CreatePayerAsync(dataSource);
        var paidAdmitted = await CreatePayerAsync(dataSource);
        var paidRejected = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(dataSource, paidAdmitted.CharacterId, 30, 1);
        await InsertOpalAsync(dataSource, paidRejected.CharacterId, 31, 1);
        var reservationId = Guid.NewGuid();
        foreach (var payer in new[]
                 {
                     freeAdmitted,
                     paidAdmitted,
                     paidRejected
                 })
        {
            await InsertDailyClaimAsync(
                dataSource,
                reservationId,
                RealmId.Tempest,
                payer.CharacterId);
        }

        Check.True(
            (await store.ChargeAsync(Request(
                reservationId,
                RealmId.Tempest,
                [paidAdmitted, paidRejected],
                Utc(5, 0)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "mixed admission charges only returning party members");
        await store.RecordAdmissionsAsync(
            reservationId,
            [freeAdmitted.CharacterId, paidAdmitted.CharacterId]);
        var settlement = await store.SettleAsync(reservationId, []);
        Check.True(
            settlement.RefundMutations.Count == 1 &&
            settlement.RefundMutations[0].CharacterId ==
                paidRejected.CharacterId,
            "daily admission markers commit the admitted payer and refund " +
            "only the rejected payer");
        var statuses = await ReadPaymentStatusesAsync(
            dataSource,
            reservationId);
        Check.True(
            statuses[paidAdmitted.CharacterId] == "committed" &&
            statuses[paidRejected.CharacterId] == "refunded" &&
            await DailyClaimAdmittedAsync(
                dataSource,
                reservationId,
                freeAdmitted.CharacterId) &&
            await DailyClaimAdmittedAsync(
                dataSource,
                reservationId,
                paidAdmitted.CharacterId) &&
            !await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                paidRejected.CharacterId),
            "mixed free/paid settlement retains every successful daily " +
            "entry and releases only the failed entry");
    }

    private static async Task AssertOwnershipHandoffRecoveryAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceOpalPaymentStore store)
    {
        var payer = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(dataSource, payer.CharacterId, 32, 1);
        var reservationId = Guid.NewGuid();
        await InsertDailyClaimAsync(
            dataSource,
            reservationId,
            RealmId.Tempest,
            payer.CharacterId);
        var originalRequest = Request(
            reservationId,
            RealmId.Tempest,
            [payer],
            Utc(5, 5));
        Check.True(
            (await store.ChargeAsync(originalRequest)).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "handoff fixture charges under its original ownership fence");

        var takeover = await InstallReplacementOwnershipAsync(
            dataSource,
            payer);
        await ExpectThrowsAsync<InvalidDataException>(
            () => store.ChargeAsync(Request(
                reservationId,
                RealmId.Tempest,
                [payer with { Ownership = takeover }],
                Utc(5, 5))),
            "charge replay rejects a changed ownership fence");
        Check.Equal(
            0,
            (await store.SettleAsync(reservationId, []))
                .RefundMutations.Count,
            "old handler settlement defers instead of refunding behind a " +
            "replacement owner");
        Check.True(
            (await ReadPaymentStatusesAsync(dataSource, reservationId))
                [payer.CharacterId] == "pending" &&
            await ReadOpalQuantityAsync(dataSource, payer.CharacterId) == 0 &&
            await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                payer.CharacterId),
            "deferred handoff keeps both payment and daily claim pending");

        Check.Equal(
            1,
            await store.RecoverCharacterAsync(
                RealmId.Tempest,
                payer.AccountId,
                payer.CharacterId,
                takeover),
            "replacement owner reconciles the crash-stale payment before " +
            "snapshot hydration");
        Check.True(
            (await ReadPaymentStatusesAsync(dataSource, reservationId))
                [payer.CharacterId] == "refunded" &&
            await ReadOpalQuantityAsync(dataSource, payer.CharacterId) == 1 &&
            !await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                payer.CharacterId),
            "login takeover restores the Opal and failed daily attempt");
    }

    private static async Task AssertMalformedRowsRejectedAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceOpalPaymentStore store)
    {
        var payer = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(dataSource, payer.CharacterId, 33, 1);
        var reservationId = Guid.NewGuid();
        await InsertDailyClaimAsync(
            dataSource,
            reservationId,
            RealmId.Tempest,
            payer.CharacterId);
        Check.True(
            (await store.ChargeAsync(Request(
                reservationId,
                RealmId.Tempest,
                [payer],
                Utc(5, 10)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "constraint fixture creates a valid pending payment");

        await ExpectThrowsAsync<PostgresException>(async () =>
        {
            await using var command = dataSource.CreateCommand(
                "UPDATE public.legacy_instance_opal_payments " +
                "SET before_state = '{}'::jsonb " +
                "WHERE reservation_id = @reservationId;");
            command.Parameters.AddWithValue(
                "reservationId",
                reservationId);
            _ = await command.ExecuteNonQueryAsync();
        }, "empty JSON cannot satisfy payment identity constraints");
        await ExpectThrowsAsync<PostgresException>(async () =>
        {
            await using var command = dataSource.CreateCommand(
                "UPDATE public.legacy_instance_opal_payments " +
                "SET payment_status = 'refunded', settled_at = now() " +
                "WHERE reservation_id = @reservationId;");
            command.Parameters.AddWithValue(
                "reservationId",
                reservationId);
            _ = await command.ExecuteNonQueryAsync();
        }, "refunded status requires non-null refund evidence");

        Check.Equal(
            1,
            (await store.SettleAsync(reservationId, []))
                .RefundMutations.Count,
            "failed malformed updates leave the valid payment recoverable");
    }

    private static async Task<PlayerOwnershipFence>
        InstallReplacementOwnershipAsync(
            NpgsqlDataSource dataSource,
            OpalPayerFixture payer)
    {
        var ownership = new PlayerOwnershipFence(
            Guid.NewGuid(),
            checked(payer.Ownership.Generation + 1));
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET checkpoint_owner_id = @ownerId,
                checkpoint_owner_generation = @generation
            WHERE account_id = @accountId
              AND id = @characterId;
            """);
        command.Parameters.AddWithValue("ownerId", ownership.OwnerId);
        command.Parameters.AddWithValue("generation", ownership.Generation);
        command.Parameters.AddWithValue("accountId", payer.AccountId);
        command.Parameters.AddWithValue("characterId", payer.CharacterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "replacement ownership fixture supersedes the charged owner");
        return ownership;
    }

    private static async Task<bool> DailyClaimAdmittedAsync(
        NpgsqlDataSource dataSource,
        Guid reservationId,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT COALESCE(bool_and(admitted_at IS NOT NULL), false) " +
            "FROM public.legacy_instance_daily_entries " +
            "WHERE reservation_id = @reservationId " +
            "AND character_id = @characterId;");
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.AddWithValue("characterId", characterId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
