using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresLegacyInstanceOpalPaymentIntegrationChecks
{
    private static async Task AssertMixedOwnershipRecoveryFenceAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceOpalPaymentStore store)
    {
        var online = await CreatePayerAsync(dataSource);
        var offline = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(dataSource, online.CharacterId, 11, 1);
        await InsertOpalAsync(dataSource, offline.CharacterId, 12, 1);

        var reservationId = Guid.NewGuid();
        foreach (var payer in new[] { online, offline })
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
                [online, offline],
                Utc(1, 0)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "mixed-ownership recovery fixture charges both payers");

        await ClearOwnershipAsync(dataSource, offline);
        Check.Equal(
            0,
            await store.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(2, 0)),
            "one online payer fences the complete stale reservation");
        var fencedStatuses = await ReadPaymentStatusesAsync(
            dataSource,
            reservationId);
        Check.True(
            fencedStatuses[online.CharacterId] == "pending" &&
            fencedStatuses[offline.CharacterId] == "pending" &&
            await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                online.CharacterId) &&
            await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                offline.CharacterId),
            "mixed ownership leaves every payment and daily claim intact");
        Check.True(
            await ReadOpalQuantityAsync(
                dataSource,
                online.CharacterId) == 0 &&
            await ReadOpalQuantityAsync(
                dataSource,
                offline.CharacterId) == 0,
            "mixed ownership refunds neither charged Opal");
        foreach (var payer in new[] { online, offline })
        {
            await AssertInventoryEvidenceAsync(
                dataSource,
                payer.CharacterId,
                expectedRevision: 1,
                expectedEvidenceRows: 1,
                expectedChargeRows: 1,
                expectedRefundRows: 0);
        }

        await ClearOwnershipAsync(dataSource, online);
        Check.Equal(
            1,
            await store.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(2, 0)),
            "all-offline recovery reconciles the stale reservation once");
        var recoveredStatuses = await ReadPaymentStatusesAsync(
            dataSource,
            reservationId);
        Check.True(
            recoveredStatuses[online.CharacterId] == "refunded" &&
            recoveredStatuses[offline.CharacterId] == "refunded" &&
            !await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                online.CharacterId) &&
            !await DailyClaimExistsAsync(
                dataSource,
                reservationId,
                offline.CharacterId),
            "all-offline recovery refunds both payments and releases both " +
            "daily claims");
        Check.True(
            await ReadOpalQuantityAsync(
                dataSource,
                online.CharacterId) == 1 &&
            await ReadOpalQuantityAsync(
                dataSource,
                offline.CharacterId) == 1,
            "all-offline recovery restores both charged Opals");
        foreach (var payer in new[] { online, offline })
        {
            await AssertInventoryEvidenceAsync(
                dataSource,
                payer.CharacterId,
                expectedRevision: 2,
                expectedEvidenceRows: 2,
                expectedChargeRows: 1,
                expectedRefundRows: 1);
        }
        Check.Equal(
            0,
            await store.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(2, 0)),
            "all-offline recovery replay is idempotent");
    }

    private static async Task AssertScopedRecoveryAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceOpalPaymentStore store)
    {
        var staleAdmitted = await CreatePayerAsync(dataSource);
        var staleRejected = await CreatePayerAsync(dataSource);
        var otherRealm = await CreatePayerAsync(dataSource);
        var fresh = await CreatePayerAsync(dataSource);
        foreach (var payer in new[]
                 {
                     staleAdmitted,
                     staleRejected,
                     otherRealm,
                     fresh
                 })
        {
            await InsertOpalAsync(dataSource, payer.CharacterId, 15, 1);
        }

        var staleReservation = Guid.NewGuid();
        var otherRealmReservation = Guid.NewGuid();
        var freshReservation = Guid.NewGuid();
        foreach (var payer in new[] { staleAdmitted, staleRejected })
        {
            await InsertDailyClaimAsync(
                dataSource,
                staleReservation,
                RealmId.Tempest,
                payer.CharacterId);
        }
        await InsertDailyClaimAsync(
            dataSource,
            otherRealmReservation,
            RealmId.Dwargon,
            otherRealm.CharacterId);
        await InsertDailyClaimAsync(
            dataSource,
            freshReservation,
            RealmId.Tempest,
            fresh.CharacterId);

        Check.True(
            (await store.ChargeAsync(Request(
                staleReservation,
                RealmId.Tempest,
                [staleAdmitted, staleRejected],
                Utc(1, 0)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "stale recovery fixture charges the Tempest party");
        Check.True(
            (await store.ChargeAsync(Request(
                otherRealmReservation,
                RealmId.Dwargon,
                [otherRealm],
                Utc(1, 0)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "stale recovery fixture charges the Dwargon payer");
        Check.True(
            (await store.ChargeAsync(Request(
                freshReservation,
                RealmId.Tempest,
                [fresh],
                Utc(3, 0)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "non-stale recovery fixture charges the fresh Tempest payer");

        await store.RecordAdmissionsAsync(
            staleReservation,
            [staleAdmitted.CharacterId]);
        Check.Equal(
            0,
            await store.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(2, 0)),
            "recovery does not compensate a still-owned online party");
        Check.True(
            (await ReadPaymentStatusesAsync(
                dataSource,
                staleReservation)).Values.All(
                    static status => status == "pending") &&
            await DailyClaimExistsAsync(
                dataSource,
                staleReservation,
                staleRejected.CharacterId),
            "online-owned stale payment and claim remain untouched");

        await ClearOwnershipAsync(dataSource, staleRejected);
        Check.Equal(
            0,
            await store.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(2, 0)),
            "background recovery fences the complete reservation while " +
            "any pending payer remains online");
        var mixedOwnershipStatuses = await ReadPaymentStatusesAsync(
            dataSource,
            staleReservation);
        Check.True(
            mixedOwnershipStatuses[staleAdmitted.CharacterId] ==
                "pending" &&
            mixedOwnershipStatuses[staleRejected.CharacterId] ==
                "pending",
            "mixed online and offline ownership mutates no pending " +
            "payment");
        Check.True(
            await DailyClaimExistsAsync(
                dataSource,
                staleReservation,
                staleAdmitted.CharacterId) &&
            await DailyClaimExistsAsync(
                dataSource,
                staleReservation,
                staleRejected.CharacterId),
            "mixed online and offline ownership mutates no daily claim");
        Check.True(
            await ReadOpalQuantityAsync(
                dataSource,
                staleAdmitted.CharacterId) == 0 &&
            await ReadOpalQuantityAsync(
                dataSource,
                staleRejected.CharacterId) == 0 &&
            await ReadInventoryRevisionAsync(
                dataSource,
                staleAdmitted.CharacterId) == 1 &&
            await ReadInventoryRevisionAsync(
                dataSource,
                staleRejected.CharacterId) == 1,
            "mixed ownership neither refunds nor advances payer inventory");

        await ClearOwnershipAsync(
            dataSource,
            staleAdmitted,
            otherRealm,
            fresh);
        Check.Equal(
            1,
            await store.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(2, 0)),
            "a later all-offline sweep recovers the complete fenced " +
            "reservation");
        var staleStatuses = await ReadPaymentStatusesAsync(
            dataSource,
            staleReservation);
        Check.True(
            staleStatuses[staleAdmitted.CharacterId] == "committed" &&
            staleStatuses[staleRejected.CharacterId] == "refunded",
            "all-offline recovery commits admission and refunds rejection");
        Check.True(
            await DailyClaimExistsAsync(
                dataSource,
                staleReservation,
                staleAdmitted.CharacterId) &&
            !await DailyClaimExistsAsync(
                dataSource,
                staleReservation,
                staleRejected.CharacterId) &&
            await ReadOpalQuantityAsync(
                dataSource,
                staleRejected.CharacterId) == 1,
            "all-offline recovery retains admission, refunds its Opal, " +
            "and releases the rejected daily claim");
        Check.True(
            (await ReadPaymentStatusesAsync(
                dataSource,
                otherRealmReservation))[otherRealm.CharacterId] ==
                    "pending" &&
            (await ReadPaymentStatusesAsync(
                dataSource,
                freshReservation))[fresh.CharacterId] == "pending",
            "recovery leaves other realms and non-stale charges untouched");
        Check.Equal(
            0,
            await ReadOpalQuantityAsync(dataSource, otherRealm.CharacterId),
            "realm-isolated pending charge remains consumed");
        Check.Equal(
            0,
            await ReadOpalQuantityAsync(dataSource, fresh.CharacterId),
            "non-stale pending charge remains consumed");
        Check.Equal(
            0,
            await store.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(2, 0)),
            "recovery replay is idempotent");

        Check.Equal(
            1,
            await store.RecoverPendingAsync(
                RealmId.Dwargon,
                Utc(2, 0)),
            "Dwargon recovery later settles its own stale reservation");
        Check.Equal(
            1,
            await ReadOpalQuantityAsync(dataSource, otherRealm.CharacterId),
            "Dwargon stale charge is refunded in its own realm");
        Check.True(
            !await DailyClaimExistsAsync(
                dataSource,
                otherRealmReservation,
                otherRealm.CharacterId),
            "Dwargon recovery releases its failed daily claim");

        Check.Equal(
            1,
            await store.RecoverPendingAsync(
                RealmId.Tempest,
                Utc(4, 0)),
            "advancing the cutoff settles the formerly fresh reservation");
        Check.Equal(
            1,
            await ReadOpalQuantityAsync(dataSource, fresh.CharacterId),
            "formerly fresh charge is refunded only after becoming stale");
    }
}
