using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWonderlandTitleChecks
{
    private static async Task CheckBlackmarketFirstIslandAsync(NpgsqlDataSource source)
    {
        foreach (var function in new[] { 59, 62, 63 })
        {
            var run = await CreateAsync(source, count: 1);
            var request = BlackmarketRequest(run, function) with { TargetIsland = 1 };
            var store = new PostgresWonderlandBlackmarketStore(source);
            var before = await BlackmarketStateAsync(source, request);
            foreach (var reservation in new[] { Guid.Empty, Guid.NewGuid() })
                Check.True((await store.ChargeAsync(request with { AdmissionReservationId = reservation }, default)).Status ==
                    WonderlandBlackmarketStatus.NotEligible,
                    "first-island recovery requires this character's durable admitted reservation");
            await MutateAsync(source, run, """
                UPDATE legacy_instance_daily_entries SET admitted_at=NULL
                WHERE character_id=@character AND reservation_id=@reservation;
                """);
            Check.True((await store.ChargeAsync(request, default)).Status == WonderlandBlackmarketStatus.NotEligible,
                "an entry claim whose transfer never completed cannot buy recovery");
            Check.Equal(before, await BlackmarketStateAsync(source, request),
                "rejected first-island recovery leaves the wallet and all command evidence untouched");
            await MutateAsync(source, run, """
                UPDATE legacy_instance_daily_entries SET admitted_at=claimed_at
                WHERE character_id=@character AND reservation_id=@reservation;
                """);

            var results = await Task.WhenAll(Enumerable.Range(0, 3)
                .Select(_ => store.ChargeAsync(request, default)));
            Check.True(results.All(result => result.Succeeded && result.Silver == 10000 - request.Cost &&
                    result.WalletRevision == 1),
                "every native recovery option works before Alpha dies and debits once under concurrent replay");
            await AssertBlackmarketWalletAsync(source, request, 10000 - request.Cost, 1,
                $"1:silver:-{request.Cost}:10000:{10000 - request.Cost}:wonderland_blackmarket_charge");
            Check.True((await store.RefundAsync(request, default)).Succeeded &&
                (await store.ChargeAsync(request, default)).Status == WonderlandBlackmarketStatus.NotEligible,
                "a rejected first-island relocation refunds once and cannot replay its old authorization");
        }
    }
}
