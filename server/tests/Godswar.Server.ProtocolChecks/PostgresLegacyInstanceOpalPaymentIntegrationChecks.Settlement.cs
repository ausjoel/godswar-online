using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresLegacyInstanceOpalPaymentIntegrationChecks
{
    private static async Task AssertSettlementCompensationAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceOpalPaymentStore store)
    {
        var admitted = await CreatePayerAsync(dataSource);
        var rejected = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(dataSource, admitted.CharacterId, 8, 2);
        await InsertOpalAsync(dataSource, rejected.CharacterId, 9, 1);
        var partialReservation = Guid.NewGuid();
        await InsertDailyClaimAsync(
            dataSource,
            partialReservation,
            RealmId.Tempest,
            admitted.CharacterId);
        await InsertDailyClaimAsync(
            dataSource,
            partialReservation,
            RealmId.Tempest,
            rejected.CharacterId);
        var partialRequest = Request(
            partialReservation,
            RealmId.Tempest,
            [admitted, rejected],
            Utc(1, 15));
        Check.True(
            (await store.ChargeAsync(partialRequest)).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "partial-settlement fixture charges its party");

        var partial = await store.SettleAsync(
            partialReservation,
            [admitted.CharacterId]);
        Check.True(
            partial.RefundMutations.Count == 1 &&
            partial.RefundMutations[0].CharacterId == rejected.CharacterId,
            "partial settlement refunds only the rejected member");
        var partialStatuses = await ReadPaymentStatusesAsync(
            dataSource,
            partialReservation);
        Check.True(
            partialStatuses[admitted.CharacterId] == "committed" &&
            partialStatuses[rejected.CharacterId] == "refunded",
            "partial settlement persists committed and refunded statuses");
        Check.Equal(
            1,
            await ReadOpalQuantityAsync(dataSource, admitted.CharacterId),
            "admitted member keeps the one-Opal charge");
        Check.Equal(
            1,
            await ReadOpalQuantityAsync(dataSource, rejected.CharacterId),
            "rejected singleton Opal is fully restored");
        Check.True(
            await DailyClaimExistsAsync(
                dataSource,
                partialReservation,
                admitted.CharacterId) &&
            !await DailyClaimExistsAsync(
                dataSource,
                partialReservation,
                rejected.CharacterId),
            "partial settlement releases only the rejected daily claim");
        await AssertInventoryEvidenceAsync(
            dataSource,
            admitted.CharacterId,
            expectedRevision: 1,
            expectedEvidenceRows: 1,
            expectedChargeRows: 1,
            expectedRefundRows: 0);
        await AssertInventoryEvidenceAsync(
            dataSource,
            rejected.CharacterId,
            expectedRevision: 2,
            expectedEvidenceRows: 2,
            expectedChargeRows: 1,
            expectedRefundRows: 1);
        Check.Equal(
            0,
            (await store.SettleAsync(
                partialReservation,
                [admitted.CharacterId])).RefundMutations.Count,
            "partial settlement replay is idempotent");
        await ExpectThrowsAsync<InvalidOperationException>(
            () => store.ChargeAsync(partialRequest),
            "mixed terminal payment statuses cannot replay as charged");

        var full = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(dataSource, full.CharacterId, 10, 3);
        var fullReservation = Guid.NewGuid();
        await InsertDailyClaimAsync(
            dataSource,
            fullReservation,
            RealmId.Tempest,
            full.CharacterId);
        Check.True(
            (await store.ChargeAsync(Request(
                fullReservation,
                RealmId.Tempest,
                [full],
                Utc(1, 20)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "full-refund fixture charges one stacked Opal");
        var fullRefund = await store.SettleAsync(fullReservation, []);
        Check.True(
            fullRefund.RefundMutations.Count == 1 &&
            fullRefund.RefundMutations[0].CharacterId == full.CharacterId,
            "full settlement failure refunds every payer");
        Check.Equal(
            3,
            await ReadOpalQuantityAsync(dataSource, full.CharacterId),
            "full refund restores the exact stacked quantity");
        Check.True(
            !await DailyClaimExistsAsync(
                dataSource,
                fullReservation,
                full.CharacterId),
            "full refund releases the consumed daily claim");
        Check.True(
            (await ReadPaymentStatusesAsync(dataSource, fullReservation))
                [full.CharacterId] == "refunded",
            "full refund persists its terminal payment status");
        await AssertInventoryEvidenceAsync(
            dataSource,
            full.CharacterId,
            expectedRevision: 2,
            expectedEvidenceRows: 2,
            expectedChargeRows: 1,
            expectedRefundRows: 1);

        var capped = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(dataSource, capped.CharacterId, 11, 1);
        var cappedReservation = Guid.NewGuid();
        await InsertDailyClaimAsync(
            dataSource,
            cappedReservation,
            RealmId.Tempest,
            capped.CharacterId);
        Check.True(
            (await store.ChargeAsync(Request(
                cappedReservation,
                RealmId.Tempest,
                [capped],
                Utc(1, 25)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "stack-cap fixture consumes its singleton Opal");
        await InsertOpalAsync(dataSource, capped.CharacterId, 12, 99);
        Check.Equal(
            1,
            (await store.SettleAsync(cappedReservation, []))
                .RefundMutations.Count,
            "refund beside a full stack remains compensatable");
        Check.Equal(
            100,
            await ReadOpalQuantityAsync(dataSource, capped.CharacterId),
            "full-stack refund returns the missing Opal in another slot");
        Check.Equal(
            99,
            await ReadMaximumOpalStackAsync(dataSource, capped.CharacterId),
            "refund never exceeds the client Opal stack cap");

        var incompatible = await CreatePayerAsync(dataSource);
        await InsertOpalAsync(
            dataSource,
            incompatible.CharacterId,
            20,
            1,
            quality: 1,
            grade: 1,
            bound: 1);
        var incompatibleReservation = Guid.NewGuid();
        await InsertDailyClaimAsync(
            dataSource,
            incompatibleReservation,
            RealmId.Tempest,
            incompatible.CharacterId);
        Check.True(
            (await store.ChargeAsync(Request(
                incompatibleReservation,
                RealmId.Tempest,
                [incompatible],
                Utc(1, 30)))).Status ==
                LegacyInstanceOpalChargeStatus.Charged,
            "semantic-refund fixture consumes its bound Opal");
        await InsertOpalAsync(
            dataSource,
            incompatible.CharacterId,
            21,
            2,
            quality: 2,
            grade: 1,
            bound: 0);
        Check.Equal(
            1,
            (await store.SettleAsync(incompatibleReservation, []))
                .RefundMutations.Count,
            "semantic refund remains compensatable");
        Check.True(
            await CountOpalStacksAsync(
                dataSource,
                incompatible.CharacterId) == 2 &&
            await ReadSemanticOpalQuantityAsync(
                dataSource,
                incompatible.CharacterId,
                quality: 1,
                grade: 1,
                bound: 1) == 1 &&
            await ReadSemanticOpalQuantityAsync(
                dataSource,
                incompatible.CharacterId,
                quality: 2,
                grade: 1,
                bound: 0) == 2,
            "refund does not launder bound or quality variants into one " +
            "stack");
    }
}
